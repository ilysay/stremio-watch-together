namespace StremioWT
{
    // JSON « à la JavaScriptSerializer » sur toutes les plateformes : les objets deviennent Dictionary<string, object>,
    // les tableaux ArrayList, les nombres int / long / decimal. Tout le code de l'appli repose sur ces types.
    sealed class JsonCodec
    {
#if NETFRAMEWORK
        readonly System.Web.Script.Serialization.JavaScriptSerializer js = new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue };
        public string Serialize(object value) => js.Serialize(value);
        public T Deserialize<T>(string json) => js.Deserialize<T>(json);
#else
        static readonly System.Text.Json.JsonSerializerOptions Options = new System.Text.Json.JsonSerializerOptions { IncludeFields = true };

        public string Serialize(object value) => System.Text.Json.JsonSerializer.Serialize(value, value?.GetType() ?? typeof(object), Options);

        public T Deserialize<T>(string json)
        {
            if (json == null) return default;
            var t = typeof(T);
            if (t == typeof(object) || t == typeof(Dictionary<string, object>) || t == typeof(List<Dictionary<string, object>>) || t == typeof(List<string>))
            {
                using (var doc = System.Text.Json.JsonDocument.Parse(json))
                {
                    var value = ToObject(doc.RootElement);
                    if (t == typeof(List<Dictionary<string, object>>))
                        return value is System.Collections.ArrayList a ? (T)(object)a.Cast<object>().Select(x => x as Dictionary<string, object>).ToList() : default;
                    if (t == typeof(List<string>))
                        return value is System.Collections.ArrayList b ? (T)(object)b.Cast<object>().Select(x => x?.ToString()).ToList() : default;
                    return value is T ok ? ok : default;
                }
            }
            return System.Text.Json.JsonSerializer.Deserialize<T>(json, Options);
        }

        static object ToObject(System.Text.Json.JsonElement e)
        {
            switch (e.ValueKind)
            {
                case System.Text.Json.JsonValueKind.Object:
                    var d = new Dictionary<string, object>();
                    foreach (var p in e.EnumerateObject()) d[p.Name] = ToObject(p.Value);
                    return d;
                case System.Text.Json.JsonValueKind.Array:
                    var a = new System.Collections.ArrayList();
                    foreach (var x in e.EnumerateArray()) a.Add(ToObject(x));
                    return a;
                case System.Text.Json.JsonValueKind.String: return e.GetString();
                case System.Text.Json.JsonValueKind.Number:
                    if (e.TryGetInt32(out var i)) return i;
                    if (e.TryGetInt64(out var l)) return l;
                    return e.GetDecimal();
                case System.Text.Json.JsonValueKind.True: return true;
                case System.Text.Json.JsonValueKind.False: return false;
                default: return null;
            }
        }
#endif
    }
}
