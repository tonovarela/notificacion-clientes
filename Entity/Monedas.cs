using System;

namespace notificacion_clientes.Entity
{
    /// <summary>
    /// La vista de antigüedad devuelve el nombre de la moneda —"Pesos", "Dolares"— y no su código.
    /// Aquí se traduce a las tres letras que usan tanto el correo como la columna Moneda de
    /// CorreosCXC.notif.EnvioFactura, que es VARCHAR(3).
    ///
    /// Vive en un solo lugar a propósito: si la regla estuviera duplicada, el día que la vista
    /// devuelva un tercer valor el correo diría una cosa y la tabla guardaría otra, y sólo se
    /// notaría al comparar los dos meses después.
    /// </summary>
    public static class Monedas
    {
        /// <summary>Código ISO de tres letras. Todo lo que no sea dólar se trata como peso.</summary>
        public static string Codigo(string? moneda) => EsDolares(moneda) ? "USD" : "MXN";

        /// <summary>
        /// Se compara por prefijo porque el valor llega como CHAR desde el ERP y ha aparecido
        /// como "Dolares" y "Dólares"; también se acepta el código por si la fuente cambia.
        /// </summary>
        public static bool EsDolares(string? moneda) =>
            moneda is not null
            && (moneda.StartsWith("Dol", StringComparison.OrdinalIgnoreCase)
                || moneda.StartsWith("Dól", StringComparison.OrdinalIgnoreCase)
                || moneda.Equals("USD", StringComparison.OrdinalIgnoreCase));
    }
}
