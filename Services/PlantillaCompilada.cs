using System;
using System.IO;
using Scriban;

namespace notificacion_clientes.Services
{
    /// <summary>
    /// Una plantilla Scriban del disco: se lee y compila una sola vez, la primera vez que se usa.
    /// Los errores de sintaxis salen aquí y no a la mitad de un envío.
    /// </summary>
    public class PlantillaCompilada
    {
        private readonly Lazy<Template> _plantilla;

        public PlantillaCompilada(string rutaPlantilla)
        {
            _plantilla = new Lazy<Template>(() =>
            {
                if (!File.Exists(rutaPlantilla))
                    throw new FileNotFoundException($"No se encontró la plantilla del correo en {rutaPlantilla}");

                var plantilla = Template.Parse(File.ReadAllText(rutaPlantilla), rutaPlantilla);

                if (plantilla.HasErrors)
                    throw new InvalidOperationException(
                        $"La plantilla {rutaPlantilla} tiene errores: {string.Join("; ", plantilla.Messages)}");

                return plantilla;
            });
        }

        /// <summary>
        /// Renderiza con los nombres del modelo tal cual: sin esto Scriban esperaría snake_case
        /// automático y el modelo anónimo dejaría de casar.
        /// </summary>
        public string Renderizar(object modelo) => _plantilla.Value.Render(modelo, miembro => miembro.Name);
    }
}
