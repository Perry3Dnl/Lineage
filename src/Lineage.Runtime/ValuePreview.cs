using System;
using System.Globalization;

namespace Lineage
{
    internal static class ValuePreview
    {
        private const int MaxLength = 80;

        public static string Of(object value)
        {
            if (value == null)
            {
                return "null";
            }

            var type = value.GetType();
            if (value is string text)
            {
                return Quote(Trim(text));
            }

            if (type.IsEnum || type.IsPrimitive || value is decimal || value is DateTime || value is TimeSpan || value is Guid)
            {
                return Convert.ToString(value, CultureInfo.InvariantCulture) ?? type.Name;
            }

            string rendered;
            try
            {
                rendered = value.ToString();
            }
            catch
            {
                return type.Name;
            }

            if (string.IsNullOrEmpty(rendered) || rendered == type.FullName || rendered == type.Name || rendered == type.ToString())
            {
                return null;
            }

            return Quote(Trim(rendered));
        }

        private static string Trim(string text)
        {
            if (text.Length <= MaxLength)
            {
                return text;
            }

            return text.Substring(0, MaxLength - 1) + "…";
        }

        private static string Quote(string text)
        {
            return "\"" + text.Replace("\"", "\\\"") + "\"";
        }
    }
}
