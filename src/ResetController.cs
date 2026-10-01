using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal interface IResetGateway
    {
        Task<UsageSnapshot> Read(CancellationToken token);
        Task<string> Consume(string idempotencyKey,string creditId,CancellationToken token);
    }

    internal static class ResetPolicy
    {
        public const double Threshold=1;
        public static bool Fresh(Profile profile,DateTime now)
        {
            DateTime at;
            return String.IsNullOrEmpty(profile.Error) && DateTime.TryParse(profile.QuotaTimeUtc,null,DateTimeStyles.RoundtripKind,out at)
                && now-at.ToUniversalTime()>=TimeSpan.Zero && now-at.ToUniversalTime()<=TimeSpan.FromSeconds(60);
        }
        public static QuotaWindow[] Windows(Profile profile)
        {
            var bucket=profile.Quotas.FirstOrDefault(q=>q.Name=="codex");
            return bucket==null?new QuotaWindow[0]:new[]{bucket.Primary,bucket.Secondary}.Where(w=>w!=null).ToArray();
        }
        public static bool Low(Profile profile,DateTime now)
        {
            double seconds=(now-new DateTime(1970,1,1,0,0,0,DateTimeKind.Utc)).TotalSeconds;
            return Windows(profile).Any(w=>w.Remaining.HasValue && w.Remaining.Value<=Threshold && (!w.ResetsAt.HasValue || w.ResetsAt.Value>seconds));
        }
        public static bool Recovered(Profile profile)
        {
            var windows=Windows(profile);
            return windows.Length>0 && windows.All(w=>w.Remaining.HasValue && w.Remaining.Value>Threshold);
        }
        public static string Fingerprint(Profile profile)
        {
            return String.Join("|",Windows(profile).Select(w=>(w.Minutes.HasValue?w.Minutes.Value.ToString(CultureInfo.InvariantCulture):"?")+":"+(w.ResetsAt.HasValue?w.ResetsAt.Value.ToString(CultureInfo.InvariantCulture):"?")+":"+(w.Remaining.HasValue?w.Remaining.Value.ToString("R",CultureInfo.InvariantCulture):"?")));
        }
    }

    internal sealed class ResetController
    {
        private readonly SemaphoreSlim gate=new SemaphoreSlim(1,1);
        private readonly Func<DateTime> clock;
        public ResetController(Func<DateTime> now=null) {clock=now ?? (()=>DateTime.UtcNow);}

        public async Task Run(Profile profile,Func<bool> authorizedAndActive,IResetGateway gateway,Action save,CancellationToken token)
        {
            await gate.WaitAsync(token);
            try {await RunLocked(profile,authorizedAndActive,gateway,save,token);}
            finally {gate.Release();}
        }
        private async Task RunLocked(Profile profile,Func<bool> authorizedAndActive,IResetGateway gateway,Action save,CancellationToken token)
        {
            DateTime now=clock();
            if(!authorizedAndActive() || !ResetPolicy.Fresh(profile,now)) return;
            ResetAttempt attempt=profile.ResetAttempt;
            if(attempt!=null && attempt.State=="noCredit" && profile.ResetCredits!=null && profile.ResetCredits.AvailableCount==0)
            {attempt.BeforeCount=0;save();}
            if(attempt!=null && (attempt.State=="accepted" || attempt.State=="pending"))
            {
                if(ResetPolicy.Recovered(profile))
                {
                    bool confirmed=attempt.State=="accepted";
                    attempt.State="recovered";
                    profile.ResetMessage=confirmed?"Reset confirmé · quotas relus auprès de Codex.":"Quotas rétablis · résultat de la tentative précédente non confirmé.";
                    save(); return;
                }
                if(attempt.State=="accepted")
                {
                    profile.ResetMessage="Reset accepté · confirmation des quotas en attente. Aucun autre crédit ne sera consommé.";
                    save(); return;
                }
            }
            if(!ResetPolicy.Low(profile,now) || profile.ResetCredits==null || !profile.ResetCredits.CanConsume(now)) return;
            string fingerprint=ResetPolicy.Fingerprint(profile);
            if(attempt!=null)
            {
                if(attempt.State=="unsupported") return;
                DateTime started;
                if(attempt.State=="recovered" && DateTime.TryParse(attempt.StartedUtc,null,DateTimeStyles.RoundtripKind,out started) && now-started.ToUniversalTime()<TimeSpan.FromMinutes(5)) return;
                if(attempt.State=="nothingToReset" && attempt.TriggerFingerprint==fingerprint) return;
                if(attempt.State=="noCredit" && attempt.BeforeCount==profile.ResetCredits.AvailableCount && (profile.ResetCredits.Next(now)==null || profile.ResetCredits.Next(now).Id==attempt.CreditId)) return;
                if(attempt.State=="pending")
                {
                    DateTime last;
                    if(attempt.SendCount>=3) {profile.ResetMessage="Résultat incertain après 3 tentatives identiques. Aucune nouvelle consommation automatique.";save();return;}
                    if(DateTime.TryParse(attempt.LastSentUtc,null,DateTimeStyles.RoundtripKind,out last) && now-last.ToUniversalTime()<TimeSpan.FromSeconds(60)) return;
                }
                else attempt=null;
            }
            if(attempt==null)
            {
                var credit=profile.ResetCredits.Next(now);
                attempt=new ResetAttempt {IdempotencyKey=Guid.NewGuid().ToString(),CreditId=credit==null?null:credit.Id,State="pending",StartedUtc=now.ToString("o"),BeforeCount=profile.ResetCredits.AvailableCount,TriggerFingerprint=fingerprint};
                profile.ResetAttempt=attempt;
            }
            token.ThrowIfCancellationRequested();
            if(!authorizedAndActive()) return;
            attempt.SendCount++;attempt.LastSentUtc=now.ToString("o");
            profile.ResetMessage="Reset automatique à 1 % · demande en cours.";
            // Durable intent BEFORE sending. On a crash or timeout the same key is reused.
            save();
            token.ThrowIfCancellationRequested();
            if(!authorizedAndActive()) {profile.ResetMessage="Reset suspendu : le compte local a changé ou l'option est désactivée.";save();return;}
            string outcome;
            try {outcome=await gateway.Consume(attempt.IdempotencyKey,attempt.CreditId,token);}
            catch
            {
                profile.ResetMessage="Résultat du reset incertain · une reprise utilisera la même demande, sans nouveau crédit.";
                save();
                if(token.IsCancellationRequested) throw new OperationCanceledException(token);
                return;
            }
            if(outcome=="reset" || outcome=="alreadyRedeemed")
            {
                attempt.State="accepted";
                profile.ResetMessage="Reset accepté · vérification des quotas…";
                save();
                try
                {
                    UsageSnapshot snapshot=await gateway.Read(token);
                    snapshot.Apply(profile,clock());
                    if(ResetPolicy.Recovered(profile)) {attempt.State="recovered";profile.ResetMessage="Reset confirmé · quotas relus auprès de Codex.";}
                    else profile.ResetMessage="Reset accepté · confirmation des quotas en attente. Aucun autre crédit ne sera consommé.";
                }
                catch {profile.ResetMessage="Reset accepté · lecture de contrôle indisponible. Aucun autre crédit ne sera consommé.";}
                save(); return;
            }
            attempt.State=outcome=="nothingToReset" || outcome=="noCredit"?outcome:"unsupported";
            profile.ResetMessage=outcome=="nothingToReset"?"Codex ne trouve pas de fenêtre éligible au reset. Nouvelle vérification si le quota change.":outcome=="noCredit"?"Codex indique qu'aucun crédit de reset n'est disponible.":"Réponse de reset non reconnue · automatisme suspendu pour ce compte.";
            save();
        }
    }
}
