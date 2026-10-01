using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;

namespace Creezio.Switcher
{
    internal static class Json
    {
        public static string Write(object value) { return new JavaScriptSerializer { MaxJsonLength = 4194304 }.Serialize(value); }
        public static T Read<T>(string value) { return new JavaScriptSerializer { MaxJsonLength = 4194304 }.Deserialize<T>(value); }
        public static Dictionary<string, object> Obj(object value) { return value as Dictionary<string, object> ?? new Dictionary<string, object>(); }
        public static object Get(object value, string key) { object result; return Obj(value).TryGetValue(key, out result) ? result : null; }
        public static string Str(object value) { return value as string ?? ""; }
        public static double? Number(object value)
        {
            if (value == null || value is bool || value is string) return null;
            try { double n = Convert.ToDouble(value, CultureInfo.InvariantCulture); return double.IsNaN(n) || double.IsInfinity(n) ? (double?)null : n; }
            catch { return null; }
        }
    }

    internal sealed class AuthIdentity
    {
        public string Key, AccountId, UserId, Email, Plan, AccessToken;
        public static AuthIdentity Parse(string text)
        {
            try
            {
                var root = Json.Read<Dictionary<string, object>>(text);
                string mode = Json.Str(Json.Get(root, "auth_mode"));
                if (mode != "" && mode != "chatgpt") throw new InvalidOperationException();
                var tokens = Json.Get(root, "tokens");
                string access = Json.Str(Json.Get(tokens, "access_token"));
                string id = Json.Str(Json.Get(tokens, "id_token"));
                string account = Json.Str(Json.Get(tokens, "account_id"));
                if (access.Length < 10 || id.Length < 10 || account.Length == 0) throw new InvalidOperationException();
                var payload = DecodeJwt(id);
                var auth = Json.Get(payload, "https://api.openai.com/auth");
                string claimAccount = Json.Str(Json.Get(auth, "chatgpt_account_id"));
                if (claimAccount.Length > 0 && claimAccount != account) throw new InvalidOperationException();
                string user = Json.Str(Json.Get(payload, "sub"));
                if (user.Length == 0) throw new InvalidOperationException();
                string email = Json.Str(Json.Get(payload, "email"));
                using (var sha = SHA256.Create())
                {
                    return new AuthIdentity {
                        Key = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(account + "\n" + user))).Replace("-", "").ToLowerInvariant(),
                        AccountId = account, UserId = user, Email = email.Length > 0 ? email : "Compte ChatGPT",
                        Plan = Json.Str(Json.Get(auth, "chatgpt_plan_type")), AccessToken = access
                    };
                }
            }
            catch { throw new InvalidOperationException("Connexion ChatGPT non reconnue. Importez un fichier auth.json généré par Codex après connexion."); }
        }
        private static object DecodeJwt(string token)
        {
            var parts = token.Split('.');
            if (parts.Length != 3) throw new FormatException();
            string encoded = parts[1].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            // Claims label a local profile; only the Codex server can validate authentication.
            return Json.Read<Dictionary<string, object>>(Encoding.UTF8.GetString(Convert.FromBase64String(encoded)));
        }
    }

    public sealed class QuotaWindow
    {
        public double? Remaining { get; set; }
        public int? Minutes { get; set; }
        public long? ResetsAt { get; set; }
        public string Caption
        {
            get {
                if (!Minutes.HasValue) return "Fenêtre";
                if (Minutes.Value == 10080) return "7 jours";
                if (Minutes.Value % 60 == 0) return (Minutes.Value / 60) + " heures";
                return Minutes.Value + " min";
            }
        }
        public string ResetCaption
        {
            get {
                if (!ResetsAt.HasValue) return "Réinitialisation non renseignée";
                try { return "Réinitialisation le " + new DateTimeOffset(1970,1,1,0,0,0,TimeSpan.Zero).AddSeconds(ResetsAt.Value).ToLocalTime().ToString("dd/MM à HH:mm"); }
                catch { return "Réinitialisation non renseignée"; }
            }
        }
    }
    public sealed class QuotaBucket
    {
        public string Name { get; set; }
        public QuotaWindow Primary { get; set; }
        public QuotaWindow Secondary { get; set; }
    }
    public sealed class Profile
    {
        public string Key { get; set; }
        public string Label { get; set; }
        public string Email { get; set; }
        public string Plan { get; set; }
        public string AuthJson { get; set; }
        public List<QuotaBucket> Quotas { get; set; }
        public string QuotaTimeUtc { get; set; }
        public string Error { get; set; }
        public Profile() { Quotas = new List<QuotaBucket>(); }
        public bool IsFresh
        {
            get { DateTime time; return String.IsNullOrEmpty(Error) && DateTime.TryParse(QuotaTimeUtc, null, DateTimeStyles.RoundtripKind, out time) && DateTime.UtcNow - time.ToUniversalTime() < TimeSpan.FromMinutes(10) && time.ToUniversalTime() <= DateTime.UtcNow.AddMinutes(1); }
        }
        public double? Score
        {
            get {
                if (!IsFresh) return null;
                var bucket = Quotas.FirstOrDefault(q => q.Name == "codex") ?? Quotas.FirstOrDefault();
                if (bucket == null) return null;
                var values = new[] { bucket.Primary, bucket.Secondary }.Where(w => w != null && w.Remaining.HasValue).Select(w => w.Remaining.Value).ToList();
                return values.Count == 0 ? (double?)null : values.Min();
            }
        }
    }
    public sealed class VaultData
    {
        public int Version { get; set; }
        public List<Profile> Profiles { get; set; }
        public string PreviousAuthJson { get; set; }
        public VaultData() { Version = 1; Profiles = new List<Profile>(); }
    }
    public sealed class Settings
    {
        public string CodexExecutable { get; set; }
        public string CodexHome { get; set; }
        public bool AutoRefresh { get; set; }
        public bool Notifications { get; set; }
        public Settings() { AutoRefresh = false; Notifications = true; }
    }
    internal static class Quotas
    {
        private static QuotaWindow Window(object value)
        {
            if (value == null) return null;
            double? used = Json.Number(Json.Get(value, "usedPercent"));
            double? minutes = Json.Number(Json.Get(value, "windowDurationMins"));
            double? resets = Json.Number(Json.Get(value, "resetsAt"));
            return new QuotaWindow { Remaining = used.HasValue ? (double?)Math.Max(0, Math.Min(100, 100 - used.Value)) : null,
                Minutes = minutes.HasValue && minutes >= 0 && minutes <= Int32.MaxValue ? (int?)minutes.Value : null,
                ResetsAt = resets.HasValue && resets >= 0 && resets <= 253402300799L ? (long?)resets.Value : null };
        }
        public static List<QuotaBucket> Parse(object result)
        {
            var map = Json.Obj(Json.Get(result, "rateLimitsByLimitId"));
            if (map.Count == 0 && Json.Get(result, "rateLimits") != null) map["codex"] = Json.Get(result, "rateLimits");
            return map.Select(pair => new QuotaBucket { Name = pair.Key, Primary = Window(Json.Get(pair.Value, "primary")), Secondary = Window(Json.Get(pair.Value, "secondary")) }).ToList();
        }
    }
}
