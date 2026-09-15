using System;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Lineage
{
    /// <summary>
    /// Captures supported value types into a compact typed payload. Primitive values do
    /// not need a formatted string on the recording path; formatting is deferred until
    /// report hydration.
    /// </summary>
    internal static class LineageValueCodec
    {
        internal struct Payload
        {
            public LineageValueKind Kind;
            public long Data0;
            public long Data1;
            public string Text;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct FloatBits
        {
            [FieldOffset(0)] public float Value;
            [FieldOffset(0)] public int Bits;
        }

        public static Payload Capture(object value, string declaredTypeName)
        {
            if (value == null)
            {
                return new Payload { Kind = LineageValueKind.Null };
            }

            var type = value.GetType();
            if (type.IsEnum)
            {
                return CaptureEnum(value, type);
            }

            switch (Type.GetTypeCode(type))
            {
                case TypeCode.Boolean:
                    return Scalar(LineageValueKind.Boolean, (bool)value ? 1 : 0);
                case TypeCode.Char:
                    return Scalar(LineageValueKind.Char, (char)value);
                case TypeCode.SByte:
                    return Scalar(LineageValueKind.SByte, (sbyte)value);
                case TypeCode.Byte:
                    return Scalar(LineageValueKind.Byte, (byte)value);
                case TypeCode.Int16:
                    return Scalar(LineageValueKind.Int16, (short)value);
                case TypeCode.UInt16:
                    return Scalar(LineageValueKind.UInt16, (ushort)value);
                case TypeCode.Int32:
                    return Scalar(LineageValueKind.Int32, (int)value);
                case TypeCode.UInt32:
                    return Scalar(LineageValueKind.UInt32, unchecked((long)(uint)value));
                case TypeCode.Int64:
                    return Scalar(LineageValueKind.Int64, (long)value);
                case TypeCode.UInt64:
                    return Scalar(LineageValueKind.UInt64, unchecked((long)(ulong)value));
                case TypeCode.Single:
                    var single = new FloatBits { Value = (float)value };
                    return Scalar(LineageValueKind.Single, single.Bits);
                case TypeCode.Double:
                    return Scalar(LineageValueKind.Double, BitConverter.DoubleToInt64Bits((double)value));
                case TypeCode.Decimal:
                    return CaptureDecimal((decimal)value);
                case TypeCode.DateTime:
                    return Scalar(LineageValueKind.DateTime, ((DateTime)value).ToBinary());
            }

            if (value is IntPtr)
            {
                return Scalar(LineageValueKind.NativeInt, ((IntPtr)value).ToInt64());
            }

            if (value is UIntPtr)
            {
                return Scalar(LineageValueKind.NativeUInt, unchecked((long)((UIntPtr)value).ToUInt64()));
            }

            if (value is TimeSpan)
            {
                return Scalar(LineageValueKind.TimeSpan, ((TimeSpan)value).Ticks);
            }

            if (value is DateTimeOffset)
            {
                var dto = (DateTimeOffset)value;
                return new Payload
                {
                    Kind = LineageValueKind.DateTimeOffset,
                    Data0 = dto.Ticks,
                    Data1 = dto.Offset.Ticks
                };
            }

            if (value is Guid)
            {
                var bytes = ((Guid)value).ToByteArray();
                return new Payload
                {
                    Kind = LineageValueKind.Guid,
                    Data0 = BitConverter.ToInt64(bytes, 0),
                    Data1 = BitConverter.ToInt64(bytes, 8)
                };
            }

            var fullName = type.FullName ?? string.Empty;
            switch (fullName)
            {
                case "System.Half":
                    return new Payload
                    {
                        Kind = LineageValueKind.Half,
                        Text = FormatCanonical(value)
                    };
                case "System.Int128":
                    return new Payload
                    {
                        Kind = LineageValueKind.Int128,
                        Text = FormatCanonical(value)
                    };
                case "System.UInt128":
                    return new Payload
                    {
                        Kind = LineageValueKind.UInt128,
                        Text = FormatCanonical(value)
                    };
                case "System.DateOnly":
                    return CaptureModernIntegralProperty(value, LineageValueKind.DateOnly, "DayNumber");
                case "System.TimeOnly":
                    return CaptureModernIntegralProperty(value, LineageValueKind.TimeOnly, "Ticks");
            }

            if (type.IsValueType)
            {
                var tuple = fullName == "System.ValueTuple" || fullName.StartsWith("System.ValueTuple`", StringComparison.Ordinal);
                return new Payload
                {
                    Kind = tuple ? LineageValueKind.ValueTuple : LineageValueKind.Struct,
                    Text = ValuePreview.Of(value)
                };
            }

            // Keep existing reference-value previews working while reference types remain
            // outside the first-class typed-value scope.
            return new Payload
            {
                Kind = LineageValueKind.LegacyText,
                Text = ValuePreview.Of(value)
            };
        }

        public static string Format(LineageValueKind kind, long data0, long data1, string text)
        {
            switch (kind)
            {
                case LineageValueKind.None:
                    return text;
                case LineageValueKind.Null:
                    return "null";
                case LineageValueKind.Boolean:
                    return data0 != 0 ? "true" : "false";
                case LineageValueKind.Char:
                    return FormatChar((char)data0);
                case LineageValueKind.SByte:
                    return ((sbyte)data0).ToString(CultureInfo.InvariantCulture);
                case LineageValueKind.Byte:
                    return ((byte)data0).ToString(CultureInfo.InvariantCulture);
                case LineageValueKind.Int16:
                    return ((short)data0).ToString(CultureInfo.InvariantCulture);
                case LineageValueKind.UInt16:
                    return ((ushort)data0).ToString(CultureInfo.InvariantCulture);
                case LineageValueKind.Int32:
                    return ((int)data0).ToString(CultureInfo.InvariantCulture);
                case LineageValueKind.UInt32:
                    return unchecked((uint)data0).ToString(CultureInfo.InvariantCulture);
                case LineageValueKind.Int64:
                    return data0.ToString(CultureInfo.InvariantCulture);
                case LineageValueKind.UInt64:
                    return unchecked((ulong)data0).ToString(CultureInfo.InvariantCulture);
                case LineageValueKind.NativeInt:
                    return data0.ToString(CultureInfo.InvariantCulture);
                case LineageValueKind.NativeUInt:
                    return unchecked((ulong)data0).ToString(CultureInfo.InvariantCulture);
                case LineageValueKind.Single:
                    var single = new FloatBits { Bits = unchecked((int)data0) };
                    return single.Value.ToString("R", CultureInfo.InvariantCulture);
                case LineageValueKind.Double:
                    return BitConverter.Int64BitsToDouble(data0).ToString("R", CultureInfo.InvariantCulture);
                case LineageValueKind.Decimal:
                    return DecodeDecimal(data0, data1).ToString(CultureInfo.InvariantCulture);
                case LineageValueKind.Enum:
                    return string.IsNullOrEmpty(text) ? data0.ToString(CultureInfo.InvariantCulture) : text;
                case LineageValueKind.DateTime:
                    return DateTime.FromBinary(data0).ToString("O", CultureInfo.InvariantCulture);
                case LineageValueKind.DateTimeOffset:
                    return new DateTimeOffset(data0, new TimeSpan(data1)).ToString("O", CultureInfo.InvariantCulture);
                case LineageValueKind.TimeSpan:
                    return new TimeSpan(data0).ToString("c", CultureInfo.InvariantCulture);
                case LineageValueKind.Guid:
                    return DecodeGuid(data0, data1).ToString("D");
                case LineageValueKind.Half:
                case LineageValueKind.Int128:
                case LineageValueKind.UInt128:
                case LineageValueKind.DateOnly:
                case LineageValueKind.TimeOnly:
                case LineageValueKind.Struct:
                case LineageValueKind.ValueTuple:
                case LineageValueKind.LegacyText:
                    return text;
                default:
                    return text;
            }
        }

        public static string ResolveTypeName(object value, string declaredTypeName)
        {
            if (!string.IsNullOrEmpty(declaredTypeName))
            {
                return declaredTypeName;
            }

            return value != null ? value.GetType().FullName : null;
        }

        private static Payload CaptureEnum(object value, Type type)
        {
            var underlying = Enum.GetUnderlyingType(type);
            var code = Type.GetTypeCode(underlying);
            long bits;
            switch (code)
            {
                case TypeCode.Byte:
                case TypeCode.UInt16:
                case TypeCode.UInt32:
                case TypeCode.UInt64:
                    bits = unchecked((long)Convert.ToUInt64(value, CultureInfo.InvariantCulture));
                    break;
                default:
                    bits = Convert.ToInt64(value, CultureInfo.InvariantCulture);
                    break;
            }

            return new Payload
            {
                Kind = LineageValueKind.Enum,
                Data0 = bits,
                Data1 = (long)code,
                Text = Enum.GetName(type, value) ?? FormatCanonical(value)
            };
        }

        private static Payload CaptureDecimal(decimal value)
        {
            var bits = decimal.GetBits(value);
            var data0 = unchecked((long)((ulong)(uint)bits[0] | ((ulong)(uint)bits[1] << 32)));
            var data1 = unchecked((long)((ulong)(uint)bits[2] | ((ulong)(uint)bits[3] << 32)));
            return new Payload
            {
                Kind = LineageValueKind.Decimal,
                Data0 = data0,
                Data1 = data1
            };
        }

        private static decimal DecodeDecimal(long data0, long data1)
        {
            var lo = unchecked((int)(uint)data0);
            var mid = unchecked((int)(uint)((ulong)data0 >> 32));
            var hi = unchecked((int)(uint)data1);
            var flags = unchecked((int)(uint)((ulong)data1 >> 32));
            var negative = (flags & unchecked((int)0x80000000)) != 0;
            var scale = (byte)((flags >> 16) & 0x7f);
            return new decimal(lo, mid, hi, negative, scale);
        }

        private static Guid DecodeGuid(long data0, long data1)
        {
            var bytes = new byte[16];
            Array.Copy(BitConverter.GetBytes(data0), 0, bytes, 0, 8);
            Array.Copy(BitConverter.GetBytes(data1), 0, bytes, 8, 8);
            return new Guid(bytes);
        }

        private static Payload CaptureModernIntegralProperty(object value, LineageValueKind kind, string propertyName)
        {
            try
            {
                var property = value.GetType().GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public);
                if (property != null)
                {
                    var raw = property.GetValue(value, null);
                    return new Payload
                    {
                        Kind = kind,
                        Data0 = raw != null ? Convert.ToInt64(raw, CultureInfo.InvariantCulture) : 0,
                        Text = FormatCanonical(value)
                    };
                }
            }
            catch
            {
            }

            return new Payload { Kind = kind, Text = FormatCanonical(value) };
        }

        private static Payload Scalar(LineageValueKind kind, long value)
        {
            return new Payload { Kind = kind, Data0 = value };
        }

        private static string FormatCanonical(object value)
        {
            var formattable = value as IFormattable;
            if (formattable != null)
            {
                try
                {
                    return formattable.ToString(null, CultureInfo.InvariantCulture);
                }
                catch
                {
                }
            }

            return Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static string FormatChar(char value)
        {
            switch (value)
            {
                case '\\': return "'\\\\'";
                case '\'': return "'\\\''";
                case '\n': return "'\\n'";
                case '\r': return "'\\r'";
                case '\t': return "'\\t'";
                case '\0': return "'\\0'";
                default:
                    return char.IsControl(value)
                        ? "'\\u" + ((int)value).ToString("X4", CultureInfo.InvariantCulture) + "'"
                        : "'" + value + "'";
            }
        }
    }
}
