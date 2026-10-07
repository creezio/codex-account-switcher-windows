using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
#if !NETCOREAPP
using System.Web.Script.Serialization;
#endif

namespace Creezio.Switcher
{
    internal static class Json
    {
#if NETCOREAPP
        public static string Write(object value) { return System.Text.Json.JsonSerializer.Serialize(value,Desktop.JsonCompatibility.Options); }
        public static T Read<T>(string value) { if(value.Length>4194304)throw new InvalidOperationException("JSON trop volumineux.");return System.Text.Json.JsonSerializer.Deserialize<T>(value,Desktop.JsonCompatibility.Options); }
#else
        public static string Write(object value) { return new JavaScriptSerializer { MaxJsonLength = 4194304 }.Serialize(value); }
        public static T Read<T>(string value) { return new JavaScriptSerializer { MaxJsonLength = 4194304 }.Deserialize<T>(value); }
#endif
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
    public sealed class ResetCredit
    {
        public string Id { get; set; }
        public string ResetType { get; set; }
        public string Status { get; set; }
        public long? ExpiresAt { get; set; }
    }
    public sealed class ResetCredits
    {
        public int? AvailableCount { get; set; }
        // Null means details were not supplied; an empty list means no detailed credit is available.
        public List<ResetCredit> Credits { get; set; }
        public ResetCredit Next(DateTime now)
        {
            double seconds=(now.ToUniversalTime()-new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalSeconds;
            return Credits==null?null:Credits.Where(c=>!String.IsNullOrWhiteSpace(c.Id) && c.ResetType=="codexRateLimits" && c.Status=="available" && (!c.ExpiresAt.HasValue || c.ExpiresAt.Value>seconds)).OrderBy(c=>c.ExpiresAt ?? Int64.MaxValue).FirstOrDefault();
        }
        public bool CanConsume(DateTime now) { return AvailableCount>0 && (Credits==null || Next(now)!=null); }
    }
    public sealed class ResetAttempt
    {
        public string IdempotencyKey { get; set; }
        public string CreditId { get; set; }
        public string State { get; set; }
        public string StartedUtc { get; set; }
        public string LastSentUtc { get; set; }
        public int SendCount { get; set; }
        public int? BeforeCount { get; set; }
        public string TriggerFingerprint { get; set; }
    }
    internal sealed class UsageSnapshot
    {
        public List<QuotaBucket> Buckets;
        public ResetCredits ResetCredits;
        public static UsageSnapshot Parse(object result)
        {
            object reset=Json.Get(result,"rateLimitResetCredits");
            ResetCredits credits=null;
            if(reset!=null)
            {
                double? count=Json.Number(Json.Get(reset,"availableCount"));
                credits=new ResetCredits {AvailableCount=count.HasValue && count>=0 && count<=Int32.MaxValue && count==Math.Truncate(count.Value)?(int?)count.Value:null};
                var rows=Json.Get(reset,"credits") as System.Collections.IEnumerable;
                if(Json.Get(reset,"credits")!=null) credits.Credits=new List<ResetCredit>();
                if(rows!=null && !(rows is string) && !(rows is System.Collections.IDictionary))
                {
                    foreach(object row in rows)
                    {
                        double? expires=Json.Number(Json.Get(row,"expiresAt"));
                        // An invalid non-null expiry is treated as expired, never as unlimited.
                        long? expiration=Json.Get(row,"expiresAt")==null?null:(expires.HasValue && expires>=0 && expires<=253402300799L?(long?)expires.Value:0L);
                        credits.Credits.Add(new ResetCredit {Id=Json.Str(Json.Get(row,"id")),ResetType=Json.Str(Json.Get(row,"resetType")),Status=Json.Str(Json.Get(row,"status")),ExpiresAt=expiration});
                    }
                }
            }
            return new UsageSnapshot {Buckets=Quotas.Parse(result),ResetCredits=credits};
        }
        public void Apply(Profile profile, DateTime now)
        {
            profile.Quotas=Buckets; profile.ResetCredits=ResetCredits;
            profile.QuotaTimeUtc=now.ToUniversalTime().ToString("o"); profile.Error=null;
        }
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
        public ResetCredits ResetCredits { get; set; }
        public ResetAttempt ResetAttempt { get; set; }
        public string ResetMessage { get; set; }
        public double ResetThreshold {get;set;}
        public string ResetWindow {get;set;}
        public bool AllInstances { get; set; }
        public List<string> InstanceIds { get; set; }
        public Profile() { Quotas = new List<QuotaBucket>(); AllInstances = true; InstanceIds = new List<string>();ResetThreshold=1;ResetWindow="all"; }
        public bool Allows(string instanceId) { return AllInstances || (InstanceIds != null && InstanceIds.Contains(instanceId)); }
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
        public List<DesktopInstance> Instances { get; set; }
        public VaultData() { Version = 2; Profiles = new List<Profile>(); Instances = new List<DesktopInstance>(); }
    }
    public sealed class DesktopInstance
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string AccountKey { get; set; }
        public bool AccountLocked { get; set; }
        public string PreviousAuthJson { get; set; }
        public bool Archived { get; set; }
        public bool IsLocal { get { return Id == "local"; } }
    }
    public sealed class Settings
    {
        public string CodexExecutable { get; set; }
        public string CodexHome { get; set; }
        public bool AutoRefresh { get; set; }
        public bool Notifications { get; set; }
        public bool AutoResetCredits { get; set; }
        public bool MaintainIntegration { get; set; }
        public int IntegrationCheckMinutes { get; set; }
        public Settings() { AutoRefresh = false; Notifications = true; AutoResetCredits = true; MaintainIntegration=true; IntegrationCheckMinutes=5; }
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
            if (map.Count == 0 && Json.Get(result, "rateLimits") != null)
            {
                object legacy=Json.Get(result,"rateLimits");
                string limitId=Json.Str(Json.Get(legacy,"limitId"));
                map[String.IsNullOrEmpty(limitId)?"codex":limitId]=legacy;
            }
            return map.Select(pair => new QuotaBucket { Name = pair.Key, Primary = Window(Json.Get(pair.Value, "primary")), Secondary = Window(Json.Get(pair.Value, "secondary")) }).ToList();
        }
    }
}
