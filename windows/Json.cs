using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace EasyDeck
{
    /// 작은 JSON 읽기/쓰기. 맥 버전(Swift JSONEncoder)과 같은 모양으로 저장해 profile.json을 서로 주고받을 수 있게 함
    static class Json
    {
        public static object Parse(string s)
        {
            try
            {
                int i = 0;
                var v = Value(s, ref i);
                Ws(s, ref i);
                if (i < s.Length) throw new FormatException("JSON 뒤에 남는 글자가 있습니다 (위치 " + i + ")");
                return v;
            }
            catch (IndexOutOfRangeException) { throw new FormatException("JSON이 중간에 끝났습니다"); }
            catch (ArgumentOutOfRangeException) { throw new FormatException("JSON이 중간에 끝났습니다"); }
        }

        static void Ws(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
        }

        static void Expect(string s, ref int i, char c)
        {
            Ws(s, ref i);
            if (s[i] != c) throw new FormatException("'" + c + "'가 있어야 합니다 (위치 " + i + ")");
            i++;
        }

        static object Value(string s, ref int i)
        {
            Ws(s, ref i);
            char c = s[i];
            switch (c)
            {
                case '{':
                    {
                        i++;
                        var d = new Dictionary<string, object>();
                        Ws(s, ref i);
                        if (s[i] == '}') { i++; return d; }
                        while (true)
                        {
                            Ws(s, ref i);
                            string k = Str(s, ref i);
                            Expect(s, ref i, ':');
                            d[k] = Value(s, ref i);
                            Ws(s, ref i);
                            if (s[i] == ',') { i++; continue; }
                            Expect(s, ref i, '}');
                            return d;
                        }
                    }
                case '[':
                    {
                        i++;
                        var l = new List<object>();
                        Ws(s, ref i);
                        if (s[i] == ']') { i++; return l; }
                        while (true)
                        {
                            l.Add(Value(s, ref i));
                            Ws(s, ref i);
                            if (s[i] == ',') { i++; continue; }
                            Expect(s, ref i, ']');
                            return l;
                        }
                    }
                case '"':
                    return Str(s, ref i);
                case 't': Lit(s, ref i, "true"); return true;
                case 'f': Lit(s, ref i, "false"); return false;
                case 'n': Lit(s, ref i, "null"); return null;
                default:
                    {
                        int st = i;
                        while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
                        if (st == i) throw new FormatException("알 수 없는 글자 '" + c + "' (위치 " + i + ")");
                        return double.Parse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture);
                    }
            }
        }

        static void Lit(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0)
                throw new FormatException("알 수 없는 값 (위치 " + i + ")");
            i += word.Length;
        }

        static string Str(string s, ref int i)
        {
            Ws(s, ref i);
            if (s[i] != '"') throw new FormatException("글자는 따옴표로 시작해야 합니다 (위치 " + i + ")");
            i++;
            var sb = new StringBuilder();
            while (true)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
        }

        // ---- 쓰기 ----

        public static string Write(object v)
        {
            var sb = new StringBuilder();
            W(v, sb, 0);
            sb.Append('\n');
            return sb.ToString();
        }

        static void W(object v, StringBuilder sb, int ind)
        {
            switch (v)
            {
                case null: sb.Append("null"); break;
                case string s: Q(s, sb); break;
                case bool b: sb.Append(b ? "true" : "false"); break;
                case Dictionary<string, object> d:
                    {
                        if (d.Count == 0) { sb.Append("{\n\n").Append(' ', ind).Append('}'); break; }
                        sb.Append("{\n");
                        var keys = d.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();
                        for (int n = 0; n < keys.Count; n++)
                        {
                            sb.Append(' ', ind + 2);
                            Q(keys[n], sb);
                            sb.Append(" : ");
                            W(d[keys[n]], sb, ind + 2);
                            if (n < keys.Count - 1) sb.Append(',');
                            sb.Append('\n');
                        }
                        sb.Append(' ', ind).Append('}');
                        break;
                    }
                case IList l:
                    {
                        if (l.Count == 0) { sb.Append("[\n\n").Append(' ', ind).Append(']'); break; }
                        sb.Append("[\n");
                        for (int n = 0; n < l.Count; n++)
                        {
                            sb.Append(' ', ind + 2);
                            W(l[n], sb, ind + 2);
                            if (n < l.Count - 1) sb.Append(',');
                            sb.Append('\n');
                        }
                        sb.Append(' ', ind).Append(']');
                        break;
                    }
                case double dd: Num(dd, sb); break;
                case float f: Num(f, sb); break;
                case int n: sb.Append(n.ToString(CultureInfo.InvariantCulture)); break;
                case long ln: sb.Append(ln.ToString(CultureInfo.InvariantCulture)); break;
                default: Q(v.ToString(), sb); break;
            }
        }

        static void Num(double d, StringBuilder sb)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append('0'); return; }
            if (d == Math.Floor(d) && Math.Abs(d) < 1e15) sb.Append(((long)d).ToString(CultureInfo.InvariantCulture));
            else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        static void Q(string s, StringBuilder sb)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---- 읽기 도우미 (값이 없거나 형식이 다르면 기본값) ----

        public static Dictionary<string, object> Obj(object o) => o as Dictionary<string, object> ?? new Dictionary<string, object>();

        public static string S(Dictionary<string, object> d, string k, string def)
            => d.TryGetValue(k, out var v) && v is string s ? s : def;

        public static double N(Dictionary<string, object> d, string k, double def)
            => d.TryGetValue(k, out var v) && v is double x ? x : def;

        public static double? NN(Dictionary<string, object> d, string k)
            => d.TryGetValue(k, out var v) && v is double x ? x : (double?)null;

        public static int I(Dictionary<string, object> d, string k, int def)
            => d.TryGetValue(k, out var v) && v is double x ? (int)Math.Round(x) : def;

        public static bool B(Dictionary<string, object> d, string k, bool def)
            => d.TryGetValue(k, out var v) && v is bool b ? b : def;

        public static List<object> L(Dictionary<string, object> d, string k)
            => d.TryGetValue(k, out var v) && v is List<object> l ? l : new List<object>();
    }
}
