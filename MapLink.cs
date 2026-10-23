using System.Net;

namespace ArkCopyBuffer;

/// <summary>Читаемые подписи для ссылок на карты; исходный текст буфера не меняется.</summary>
internal static class MapLink
{
    internal static bool TryDescribe(string text, out string title, out string detail)
    {
        title = detail = "";
        if (text.Length > 8192 || text.Contains('\n')) return false;
        var s = text.Trim();
        // Копирование из Markdown иногда даёт [полный URL](полный URL).
        if (s.StartsWith("[http", StringComparison.OrdinalIgnoreCase) && s.EndsWith(')'))
        {
            int end = s.IndexOf("](", StringComparison.Ordinal);
            if (end > 1) s = s[1..end];
        }
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https")) return false;

        var host = uri.Host.ToLowerInvariant();
        bool google = host is "google.com" or "google.ru" or "google.rs" or "google.co.uk" or "maps.google.com"
            || host.EndsWith(".google.com", StringComparison.Ordinal);
        bool yandex = host is "yandex.ru" or "yandex.com" or "yandex.kz" or "yandex.by" or "yandex.com.tr"
            || host.EndsWith(".yandex.ru", StringComparison.Ordinal);
        if (!google && !yandex) return false;

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (google && (parts.Length == 0 || parts[0] != "maps") && host != "maps.google.com") return false;
        if (yandex && (parts.Length == 0 || parts[0] != "maps")) return false;
        title = google ? "Google Карты" : "Яндекс Карты";
        int action = google && host != "maps.google.com" ? 1 : google ? 0 : 1;
        string Get(string key)
        {
            foreach (var pair in uri.Query.TrimStart('?').Split('&'))
            {
                int equals = pair.IndexOf('=');
                if (equals > 0 && pair[..equals].Equals(key, StringComparison.OrdinalIgnoreCase))
                    return Clean(WebUtility.UrlDecode(pair[(equals + 1)..]));
            }
            return "";
        }

        if (google)
        {
            if (parts.Length > action && parts[action] == "dir")
            {
                var stops = parts.Skip(action + 1).TakeWhile(p => !p.StartsWith('@') && p != "data")
                    .Select(Decode).Where(p => p.Length > 0).ToArray();
                var from = Get("origin");
                var to = Get("destination");
                if (from.Length == 0 && stops.Length > 0) from = stops[0];
                if (to.Length == 0 && stops.Length > 1) to = stops[^1];
                title += " · маршрут";
                detail = from.Length > 0 && to.Length > 0 ? $"{from} → {to}"
                    : to.Length > 0 ? $"До {to}" : from.Length > 0 ? $"От {from}" : "Маршрут";
                return true;
            }
            if (parts.Length > action + 1 && parts[action] is "place" or "search")
                detail = Decode(parts[action + 1]);
            if (detail.Length == 0) detail = Get("query");
            if (detail.Length == 0) detail = Get("q");
        }
        else
        {
            var route = Get("rtext").Split('~');
            if (route.Length >= 2 && route.Any(p => p.Length > 0))
            {
                title += " · маршрут";
                var from = Clean(route[0]);
                var to = Clean(route[^1]);
                detail = from.Length > 0 && to.Length > 0 ? $"{from} → {to}"
                    : to.Length > 0 ? $"До {to}" : $"От {from}";
                return true;
            }
            int place = Array.FindIndex(parts, action, p => p is "org" or "geo");
            if (place >= 0 && parts.Length > place + 1)
                detail = Decode(parts[place + 1]).Replace('_', ' ');
            if (detail.Length == 0) detail = Get("text");
            if (detail.Length == 0) detail = Get("whatshere[point]");
        }
        title += detail.Length > 0 ? " · место" : "";
        if (detail.Length == 0) detail = "Карта";
        return true;
    }

    static string Decode(string value) => Clean(WebUtility.UrlDecode(value.Replace("+", " ")));
    static string Clean(string value) => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
