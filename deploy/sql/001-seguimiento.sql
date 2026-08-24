/*
    Esquema de seguimiento de notificaciones: quien recibio que, quien contesto y a quien se le
    insistio. Sustituye a la bitacora de texto plano como fuente consultable.

    Vive en su propia base, CorreosCXC, y no dentro de Lito: son datos de la aplicacion de
    correos, no del ERP. Separarlos deja que el DBA le de permiso de ESCRITURA al usuario aqui
    sin tocar los permisos que tiene sobre Lito y LitoCRM, donde solo lee.

    Idempotente: se puede correr las veces que haga falta sin danar lo que ya exista. Cada objeto
    va detras de su propia guarda, no de una sola al principio.

    Se ejecuta con sqlcmd sin indicar base; el propio script se cambia a la suya:
        sqlcmd -S SERVIDOR -i deploy/sql/001-seguimiento.sql
*/

IF DB_ID('CorreosCXC') IS NULL
    CREATE DATABASE CorreosCXC;
GO

USE CorreosCXC;
GO

IF SCHEMA_ID('notif') IS NULL
    EXEC('CREATE SCHEMA notif');
GO

IF OBJECT_ID('notif.Envio') IS NULL
CREATE TABLE notif.Envio (
    IdEnvio            INT IDENTITY(1,1) CONSTRAINT PK_notif_Envio PRIMARY KEY,
    Cliente            VARCHAR(20)      NOT NULL,
    RazonSocial        NVARCHAR(255)    NULL,
    -- Sin <>, tal como lo expone MimeKit. 255 cabe en un indice unico (limite 900 bytes).
    MessageId          VARCHAR(255)     NOT NULL,
    -- Viaja como header X-Notificacion-Id: es el id que SI controlamos nosotros.
    Token              UNIQUEIDENTIFIER NOT NULL,
    -- Que proceso lo genero: CLIENTES (facturas del dia) o COBRANZA (estado de cuenta vencido).
    -- Sin esta columna los envios de cobranza recibirian recordatorios de facturas del dia, y no
    -- habria como saber quien contesto el correo del martes para excluirlo el viernes.
    Proceso            VARCHAR(20)      NOT NULL CONSTRAINT DF_notif_Envio_Proceso DEFAULT 'CLIENTES',
    -- NULL = envio original. Si trae valor, es el recordatorio de ese envio.
    IdEnvioOriginal    INT              NULL REFERENCES notif.Envio(IdEnvio),
    Intento            TINYINT          NOT NULL CONSTRAINT DF_notif_Envio_Intento DEFAULT 1,
    Asunto             NVARCHAR(500)    NOT NULL,
    Destinatarios      NVARCHAR(2000)   NOT NULL,
    -- Los envios en modo prueba jamas llegaron al cliente: no generan recordatorio.
    ModoPrueba         BIT              NOT NULL,
    FechaEnvio         DATETIME2(0)     NOT NULL,
    Estado             VARCHAR(16)      NOT NULL,
    Error              NVARCHAR(1000)   NULL,
    FechaRespuesta     DATETIME2(0)     NULL,
    RespondioEmail     NVARCHAR(320)    NULL,
    RespuestaMessageId VARCHAR(255)     NULL,
    RespuestaAsunto    NVARCHAR(500)    NULL,
    CONSTRAINT UQ_notif_Envio_MessageId UNIQUE (MessageId),
    CONSTRAINT CK_notif_Envio_Estado CHECK (Estado IN
        ('ENVIADO','FALLIDO','CONTESTADO','RECORDADO','SIN_RESPUESTA')),
    CONSTRAINT CK_notif_Envio_Proceso CHECK (Proceso IN ('CLIENTES','COBRANZA'))
);
GO

/*
    Migracion para una tabla creada con una version anterior del script, cuando Proceso no
    existia. Sobre una tabla recien creada no hace nada, porque el CREATE TABLE ya la trae.

    NO se juntan las dos ALTER en un solo batch: el CHECK referencia una columna que se agrega en
    la sentencia anterior, y dentro del mismo batch eso falla con 'Invalid column name'. Cada una
    va en su batch y con su propia guarda.

    Los renglones que ya existan quedan como CLIENTES, que es lo correcto: son de --clientes,
    el unico proceso que registraba antes de que existiera esta columna.
*/
IF COL_LENGTH('notif.Envio', 'Proceso') IS NULL
    ALTER TABLE notif.Envio
        ADD Proceso VARCHAR(20) NOT NULL CONSTRAINT DF_notif_Envio_Proceso DEFAULT 'CLIENTES';
GO

IF NOT EXISTS (SELECT 1 FROM sys.check_constraints
               WHERE name = 'CK_notif_Envio_Proceso'
                 AND parent_object_id = OBJECT_ID('notif.Envio'))
    ALTER TABLE notif.Envio
        ADD CONSTRAINT CK_notif_Envio_Proceso CHECK (Proceso IN ('CLIENTES','COBRANZA'));
GO

/*
    Los recordatorios que ha recibido cada envio.

    El recordatorio NO genera renglon en Envio: si lo hiciera, cada viernes agregaria otro MovID
    repetido a EnvioFactura y el estado del cliente quedaria partido entre varios envios. Pero sin
    guardar nada, su Message-Id se pierde y la respuesta del cliente no casa con nada.

    Se guarda aqui, y en una tabla y no en una columna de Envio a proposito: una columna sola
    retiene unicamente el ultimo recordatorio, y un cliente que arrastra el correo viejo en su
    bandeja y contesta ahi quedaria sin detectar. Cada renglon es "a este envio lo cubrio este
    recordatorio", y se conservan todos.

    Un mismo recordatorio puede abarcar facturas de semanas distintas —y por tanto varios envios—:
    todos comparten MessageId, asi que una sola respuesta los cierra de golpe.
*/
IF OBJECT_ID('notif.EnvioRecordatorio') IS NULL
CREATE TABLE notif.EnvioRecordatorio (
    IdEnvio    INT          NOT NULL REFERENCES notif.Envio(IdEnvio),
    -- Sin <>, igual que Envio.MessageId: es la llave del cruce contra el buzon.
    MessageId  VARCHAR(255) NOT NULL,
    -- Cuando salio ese recordatorio. Es lo unico que sabe que tan reciente es el contacto con
    -- el cliente, porque Envio.FechaEnvio se queda con la fecha del primer aviso.
    FechaEnvio DATETIME2(0) NOT NULL,
    CONSTRAINT PK_notif_EnvioRecordatorio PRIMARY KEY (IdEnvio, MessageId)
);
GO

/*
    La conciliacion busca por MessageId para cerrar el grupo entero. Sin indice es un scan por
    cada respuesta detectada.
*/
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_notif_EnvioRecordatorio_MessageId'
                                       AND object_id = OBJECT_ID('notif.EnvioRecordatorio'))
    DROP INDEX IX_notif_EnvioRecordatorio_MessageId ON notif.EnvioRecordatorio;
GO

CREATE INDEX IX_notif_EnvioRecordatorio_MessageId ON notif.EnvioRecordatorio (MessageId)
    INCLUDE (IdEnvio, FechaEnvio);
GO

/*
    Migracion desde la version anterior, que guardaba el ultimo recordatorio en una columna de
    Envio. Se copia lo que haya antes de tirar la columna, para no perder los sellos ya puestos.

    La fecha exacta de aquel recordatorio no se guardaba, asi que se aproxima con la del envio.
    Es lo peor que puede pasar: un sello viejo con fecha conservadora.
*/
IF COL_LENGTH('notif.Envio', 'RecordatorioMessageId') IS NOT NULL
    EXEC('
        INSERT INTO notif.EnvioRecordatorio (IdEnvio, MessageId, FechaEnvio)
        SELECT e.IdEnvio, e.RecordatorioMessageId, e.FechaEnvio
        FROM notif.Envio e
        WHERE e.RecordatorioMessageId IS NOT NULL
          AND NOT EXISTS (SELECT 1 FROM notif.EnvioRecordatorio r
                          WHERE r.IdEnvio = e.IdEnvio AND r.MessageId = e.RecordatorioMessageId);');
GO

IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_notif_Envio_RecordatorioMessageId'
                                       AND object_id = OBJECT_ID('notif.Envio'))
    DROP INDEX IX_notif_Envio_RecordatorioMessageId ON notif.Envio;
GO

IF COL_LENGTH('notif.Envio', 'RecordatorioMessageId') IS NOT NULL
    ALTER TABLE notif.Envio DROP COLUMN RecordatorioMessageId;
GO

/*
    Cubre la consulta de pendientes, que es la que corre todos los dias.

    Se recrea en vez de crearse solo si falta: un IF NOT EXISTS deja el indice viejo intacto
    cuando su definicion cambia, y el script pareceria haber corrido bien sin haber hecho nada.
    Un indice es un artefacto de rendimiento, tirarlo y rehacerlo no cuesta datos.
*/
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_notif_Envio_Pendientes'
                                       AND object_id = OBJECT_ID('notif.Envio'))
    DROP INDEX IX_notif_Envio_Pendientes ON notif.Envio;
GO

CREATE INDEX IX_notif_Envio_Pendientes ON notif.Envio
    (Proceso, Estado, ModoPrueba, FechaEnvio) INCLUDE (Cliente, Intento, MessageId);
GO

/*
    Que facturas iban en cada correo: sirve para reenviar los mismos adjuntos y para no volver a
    abrir un envio por una factura ya notificada.
*/
IF OBJECT_ID('notif.EnvioFactura') IS NULL
CREATE TABLE notif.EnvioFactura (
    IdEnvio INT           NOT NULL REFERENCES notif.Envio(IdEnvio),
    MovID   VARCHAR(50)   NOT NULL,
    Total   DECIMAL(18,4) NOT NULL,
    Moneda  VARCHAR(3)    NOT NULL,
    CONSTRAINT PK_notif_EnvioFactura PRIMARY KEY (IdEnvio, MovID)
);
GO

/*
    ObtenerFacturasYaNotificadas pregunta por MovID sobre envios que no estan FALLIDO. Sin este
    indice esa consulta escanea la tabla una vez por corrida, todos los dias.
*/
IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'IX_notif_EnvioFactura_MovID'
                                       AND object_id = OBJECT_ID('notif.EnvioFactura'))
    DROP INDEX IX_notif_EnvioFactura_MovID ON notif.EnvioFactura;
GO

CREATE INDEX IX_notif_EnvioFactura_MovID ON notif.EnvioFactura (MovID) INCLUDE (IdEnvio);
GO

/*
    Cuando termino bien la ultima corrida de --respuestas. Es el piso de la ventana de busqueda
    en el buzon.

    Sin el, la ventana arranca en el ultimo contacto con el cliente, que con la cadencia semanal
    de cobranza son dias. Si --respuestas deja de correr una semana, una respuesta que llego
    durante el paro y es anterior al ultimo recordatorio caeria fuera del DeliveredAfter de la
    siguiente corrida y no se recuperaria nunca. Con el piso, la primera corrida que vuelva
    barre desde donde se quedo la ultima que si termino.

    Guarda el INICIO de la corrida, no su fin: lo que llego mientras esa corrida leia el buzon
    pudo no alcanzar a verse, y el siguiente barrido tiene que volver a cubrirlo.

    Un solo renglon. El CHECK lo garantiza: con dos, la consulta tendria que decidir cual es el
    piso, que es justo la ambiguedad que se quiere evitar.
*/
IF OBJECT_ID('notif.Conciliacion') IS NULL
CREATE TABLE notif.Conciliacion (
    Id     BIT          NOT NULL CONSTRAINT PK_notif_Conciliacion PRIMARY KEY
                                 CONSTRAINT CK_notif_Conciliacion_UnRenglon CHECK (Id = 1),
    Inicio DATETIME2(0) NOT NULL
);
GO

/*
    Permisos. La aplicacion se conecta con Database=Lito en la cadena de conexion y llega aqui
    por nombre de tres partes (CorreosCXC.notif.Envio), asi que su login necesita un usuario en
    esta base. Ajusta el nombre del login al real antes de descomentar.

    IF NOT EXISTS (SELECT 1 FROM sys.database_principals WHERE name = 'USUARIO_APP')
        CREATE USER USUARIO_APP FOR LOGIN USUARIO_APP;
    GO

    ALTER ROLE db_datareader ADD MEMBER USUARIO_APP;
    ALTER ROLE db_datawriter ADD MEMBER USUARIO_APP;
    GO
*/
