using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Peach.Core;
using Peach.Core.Dom;
using Peach.Core.IO;
using Peach.Pro.Core.Dom;
using Peach.Pro.Core.Mutators.Utility;
using Peach.LLM.Core.Dom;
using Random = Peach.Core.Random;

[assembly: PluginAssembly]

namespace Peach.LLM.Core
{
    public static class Extensions
    {

        static public byte[] Bytes(this DataElement obj)
        {
            if (obj is Number &&
                (obj.InternalValue.GetVariantType() == Variant.VariantType.Int ||
                 obj.InternalValue.GetVariantType() == Variant.VariantType.Long ||
                 obj.InternalValue.GetVariantType() == Variant.VariantType.ULong))
            {
                var num = obj as Number;
                var size = (num.lengthAsBits < 8 ? 8 : num.lengthAsBits) / 8;
                var val = (ulong)num.InternalValue;
                var bytes = new byte[size];
                for (int i = 0; i < size; i++)
                {
                    bytes[size - i - 1] = (byte)(val & 0xFF);
                    val >>= 8;
                }
                return bytes;
            }
            if (obj is Peach.Core.Dom.String && obj.InternalValue.GetVariantType() == Variant.VariantType.String)
            {
                var str = obj as Peach.Core.Dom.String;
                return System.Text.Encoding.ASCII.GetBytes((string)str.InternalValue);
            }
            try
            {
                var bs = (BitwiseStream)obj.InternalValue;
                if (bs != null)
                {
                    long pos = bs.PositionBits;
                    bs.SeekBits(0, SeekOrigin.Begin);
                    var ms = new MemoryStream();
                    bs.CopyTo(ms);
                    bs.SeekBits(pos, SeekOrigin.Begin);
                    return ms.ToArray();
                }
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("Exception in Bytes(): " + e.ToString());
                Console.Error.WriteLine(e.StackTrace);
            }
            return new byte[0];
        }

        static public bool IsIn(this DataElement obj, string name)
        {
            var o = obj;
            while (o != null)
            {
                if (o.Name == name || o.referenceName == name || o.Name.Contains("_" + name) || (o.referenceName != null && o.referenceName.Contains("_" + name)))
                    return true;
                o = o.parent;
            }
            return false;
        }

        public static void Dump(this byte[] bytes, TextWriter writer = null)
        {
            if (writer == null)
                writer = Console.Out;
            if (bytes == null)
                return;
            var len = bytes.Length;
            if (len == 0)
                return;

            var sb = new StringBuilder();

            for (int i = 0; i < len; i += 16)
            {
                sb.AppendFormat("{0:X8}   ", i);

                for (int j = 0; j < 16; j++)
                {
                    if (i + j < len)
                    {
                        sb.AppendFormat("{0:X2} ", bytes[i + j]);
                    }
                    else
                    {
                        sb.Append("   ");
                    }

                    if (j == 7) sb.Append(" ");
                }

                sb.Append("  ");

                for (int j = 0; j < 16; j++)
                {
                    if (i + j < len)
                    {
                        byte b = bytes[i + j];
                        char c = (b > 31 && b < 127) ? (char)b : '.';
                        sb.Append(c);
                    }
                }

                sb.AppendLine();
            }

            writer.WriteLine(sb.ToString());
        }

        public static void DumpDiff(this byte[] left, byte[] right, TextWriter writer = null)
        {
            if (writer == null)
                writer = Console.Out;

            DumpWithHighlight(left, right);
            writer.WriteLine("==============================================================================");
            DumpWithHighlight(right, left);
        }

        private static void DumpWithHighlight(byte[] source, byte[] reference, TextWriter writer = null)
        {
            if (writer == null)
                writer = Console.Out;

            if (source == null) return;

            var len = source.Length;
            var originalColor = Console.ForegroundColor;

            for (int i = 0; i < len; i += 16)
            {
                Console.ForegroundColor = originalColor;
                writer.Write("{0:X8}   ", i);

                for (int j = 0; j < 16; j++)
                {
                    int currentPos = i + j;
                    if (currentPos < len)
                    {
                        bool isDiff = (reference == null) || (currentPos >= reference.Length) || (source[currentPos] != reference[currentPos]);

                        if (isDiff) Console.ForegroundColor = ConsoleColor.Red;
                        else Console.ForegroundColor = originalColor;

                        writer.Write("{0:X2} ", source[currentPos]);
                    }
                    else
                    {
                        writer.Write("   ");
                    }

                    if (j == 7) writer.Write(" ");
                }

                writer.Write("  ");

                for (int j = 0; j < 16; j++)
                {
                    int currentPos = i + j;
                    if (currentPos < len)
                    {
                        byte b = source[currentPos];
                        char c = (b > 31 && b < 127) ? (char)b : '.';

                        bool isDiff = (reference == null) || (currentPos >= reference.Length) || (source[currentPos] != reference[currentPos]);

                        if (isDiff) Console.ForegroundColor = ConsoleColor.Red;
                        else Console.ForegroundColor = originalColor;

                        writer.Write(c);
                    }
                }

                writer.WriteLine();
            }

            Console.ForegroundColor = originalColor;
        }

        public static void SetUniqChild(this Peach.Core.Dom.Array arr, Variant v)
        {
            if (arr.Count > 1)
                throw new PeachException("SetUniqChild can only be called on an Array with zero or one child elements.");
            if (arr.Count == 0)
            {
                var elem = arr.OriginalElement.ShallowClone(arr, arr.Name + "_0");
                elem.SetValue(v);
                arr.Add(elem);
            }
            else
            {
                arr[0].SetValue(v);
            }
        }

        public static void SetValue(this Peach.Core.Dom.DataElement elem, Variant v)
        {
            if (v.GetVariantType() == Variant.VariantType.String && !(elem is Peach.Core.Dom.String))
            {
                v = new Variant(System.Text.Encoding.ASCII.GetBytes((string)v));
            }
            elem.MutatedValue = v;
            elem.mutationFlags = MutateOverride.None;
        }
    }
}
