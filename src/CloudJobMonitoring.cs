using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Creezio.Switcher
{
    internal sealed class CloudJobMonitorEvent
    {
        public string TimeUtc {get;set;}
        public string Kind {get;set;}
        public string Message {get;set;}
    }
    internal sealed class CloudJobMonitorSnapshot
    {
        public string LastCheckedUtc {get;set;}
        public string LastSuccessfulCheckUtc {get;set;}
        public string LastKnownState {get;set;}
        public string NextCheckUtc {get;set;}
        public string LastCheckError {get;set;}
        public string NotificationKind {get;set;}
        public string NotificationState {get;set;}
        public string NotificationSentUtc {get;set;}
        public string NotificationNextAttemptUtc {get;set;}
        public string SourceReturnNextCheckUtc {get;set;}
        public int NotificationAttempts {get;set;}
        public string LastNotificationError {get;set;}
        public CloudJobMonitorEvent[] Events {get;set;}
    }
    internal sealed class CloudJobMonitorRecord
    {
        public int Version {get;set;}
        public string Id {get;set;}
        public string Binding {get;set;}
        public string State {get;set;}
        public string LastCheckedUtc {get;set;}
        public string LastSuccessfulCheckUtc {get;set;}
        public string LastKnownState {get;set;}
        public string NextCheckUtc {get;set;}
        public string LastCheckError {get;set;}
        public string NotificationKey {get;set;}
        public string NotificationKind {get;set;}
        public string NotificationState {get;set;}
        public string NotificationMarker {get;set;}
        public string NotificationSentUtc {get;set;}
        public string NotificationNextAttemptUtc {get;set;}
        public int NotificationAttempts {get;set;}
        public string LastNotificationError {get;set;}
        public List<string> NotifiedInterventions {get;set;}
        public string SourceReturnNextCheckUtc {get;set;}
        public List<CloudJobMonitorEvent> Events {get;set;}
    }
    internal sealed class CloudJobSourceNoticeSlot
    {
        public string Binding {get;set;}
        public string NextAttemptUtc {get;set;}
    }
    // Encrypted sidecars preserve the existing schema 1/2 jobs. Read is entirely
    // local: opening a dashboard never polls Cloud or delivers a message.
    internal sealed class CloudJobMonitoring
    {
        internal const int IntervalSeconds=60;
        internal const int MaximumEvents=100;
        private readonly RelayStore store;
        private readonly Func<DateTime> utcNow;
        internal CloudJobMonitoring(RelayStore store,Func<DateTime> utcNow=null)
        {this.store=store;this.utcNow=utcNow??(()=>DateTime.UtcNow);}
        private DateTime Now {get{return utcNow().ToUniversalTime();}}
        private static string Hash(string value)
        {using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(value??""))).Replace("-","").ToLowerInvariant();}
        private static string HashFields(string[] fields)
        {var value=new StringBuilder();foreach(string field in fields){string text=field??"";value.Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text);}return Hash(value.ToString());}
        private static string Binding(CloudJob job)
        {return HashFields(new[]{job.Id,job.Fingerprint,job.InstanceId,job.AccountKey,job.ConfigurationId,job.ConfigurationVersion,job.ConfigurationReadyVersion,job.ReturnToSource?"true":"false",job.SourceChannel,job.SourceThread,job.SourceAccount,(job.SourceHome??"").TrimEnd('\\','/').ToUpperInvariant()});}
        private static string SourceBinding(CloudJob job)
        {return HashFields(new[]{job.SourceThread,job.SourceAccount,(job.SourceHome??"").TrimEnd('\\','/').ToUpperInvariant()});}
        private static string Record(string id){RelayStore.MessageId(id);return "cloud-monitor-"+id+".dpapi";}
        private static string SourceRecord(CloudJob job){return "cloud-notice-source-"+SourceBinding(job)+".dpapi";}
        private CloudJobMonitorRecord Load(CloudJob job)
        {
            var value=store.ReadRecord<CloudJobMonitorRecord>(Record(job.Id));
            if(value.Version==0){value.Version=1;value.Id=job.Id;value.Binding=Binding(job);value.Events=new List<CloudJobMonitorEvent>();value.NotifiedInterventions=new List<string>();}
            if(value.Version!=1||value.Id!=job.Id||value.Binding!=Binding(job))throw new InvalidOperationException("Le journal de suivi ne correspond plus à cette mission.");
            value.Events=value.Events??new List<CloudJobMonitorEvent>();value.NotifiedInterventions=value.NotifiedInterventions??new List<string>();
            ApplySignal(value,job);return value;
        }
        private static string Signal(CloudJob job)
        {
            if(!job.ReturnToSource||String.IsNullOrEmpty(job.SourceThread))return null;
            if(job.State=="completed"||job.State=="failed")return "result:"+job.State+":"+(job.TurnId??"");
            if(job.State=="attention"||job.State=="uncertain"||job.State=="prepared"&&!String.IsNullOrWhiteSpace(job.Error)){
                string reason=job.Error??"";const string suffix=" · requête ";int at=reason.LastIndexOf(suffix,StringComparison.Ordinal);
                // The backend appends an observation-specific request ID. It
                // belongs in diagnostics, not in the identity of an alert.
                if(at>=0&&Regex.IsMatch(reason.Substring(at+suffix.Length),@"^req_[A-Za-z0-9_-]{1,180}$"))reason=reason.Substring(0,at);
                return "intervention:"+Hash((job.TurnId??job.ThreadId??"pending")+"|"+reason);
            }
            return null;
        }
        internal static bool PotentialNotification(CloudJob job)
        {return job.ReturnState=="pending"||job.ReturnState=="sending"||job.ReturnState=="uncertain"||Signal(job)!=null&&job.State!="completed"&&job.State!="failed";}
        private static void ApplySignal(CloudJobMonitorRecord value,CloudJob job)
        {
            string signal=Signal(job);
            if(signal==null){
                // A cancelled request or a resumed intervention must not keep
                // an obsolete notification alive. Its audit remains in Events.
                if(value.NotificationKey==null||value.NotificationKind=="intervention"||job.State=="cancelled"){
                    value.NotificationKey=null;value.NotificationKind=null;value.NotificationMarker=null;value.NotificationSentUtc=null;value.NotificationNextAttemptUtc=null;value.LastNotificationError=null;
                    value.NotificationState=job.ReturnToSource&&job.State!="cancelled"?"waiting":"none";
                }
                return;
            }
            if(value.NotificationKey==signal)return;
            value.NotificationKey=signal;value.NotificationKind=signal.StartsWith("result:",StringComparison.Ordinal)?"result":"intervention";
            value.NotificationMarker=value.NotificationKind=="result"?"[CREEZIO_CLOUD_RESULT:"+job.Id+"]":"[CREEZIO_CLOUD_ATTENTION:"+job.Id+":"+Hash(signal).Substring(0,16)+"]";
            value.NotificationSentUtc=null;value.NotificationNextAttemptUtc=null;value.LastNotificationError=null;
            value.NotificationState=value.NotificationKind=="result"&&(job.ReturnState=="delivered"||job.ReturnState=="uncertain"||job.ReturnState=="sending")?job.ReturnState:value.NotifiedInterventions.Contains(signal)?"delivered":"pending";
        }
        private void Event(CloudJobMonitorRecord value,string kind,string message)
        {
            value.Events.Add(new CloudJobMonitorEvent{TimeUtc=Now.ToString("o"),Kind=kind,Message=message});
            if(value.Events.Count>MaximumEvents)value.Events.RemoveRange(0,value.Events.Count-MaximumEvents);
        }
        private void Save(CloudJob job,CloudJobMonitorRecord value){store.WriteRecord(Record(job.Id),value);}
        private void Edit(CloudJob job,Action<CloudJobMonitorRecord> change)
        {using(store.Lease("cloud-monitor-"+job.Id)){var value=Load(job);change(value);Save(job,value);}}
        internal CloudJobMonitorSnapshot Read(CloudJob job)
        {
            try{
                var value=Load(job);string next=value.NotificationNextAttemptUtc;
                var slot=store.ReadRecord<CloudJobSourceNoticeSlot>(SourceRecord(job));if(slot.Binding==SourceBinding(job)&&String.CompareOrdinal(slot.NextAttemptUtc,next)>0)next=slot.NextAttemptUtc;
                if(!PendingState(value.NotificationState))next=null;
                return new CloudJobMonitorSnapshot{LastCheckedUtc=value.LastCheckedUtc,LastSuccessfulCheckUtc=value.LastSuccessfulCheckUtc,LastKnownState=value.LastKnownState,NextCheckUtc=CloudJobWorker.RuntimePending(job)?value.NextCheckUtc:null,LastCheckError=value.LastCheckError,NotificationKind=value.NotificationKind,NotificationState=value.NotificationState,NotificationSentUtc=value.NotificationSentUtc,NotificationNextAttemptUtc=next,SourceReturnNextCheckUtc=value.SourceReturnNextCheckUtc,NotificationAttempts=value.NotificationAttempts,LastNotificationError=value.LastNotificationError,Events=value.Events.ToArray()};
            }catch{return new CloudJobMonitorSnapshot{NotificationState="attention",LastCheckError="Le journal de suivi est illisible ou ne correspond plus à la mission. Aucun envoi automatique autorisé.",Events=new CloudJobMonitorEvent[0]};}
        }
        internal void Observe(CloudJob job)
        {Edit(job,value=>{if(value.State==job.State)return;value.State=job.State;Event(value,"state","État de la mission : "+job.State+".");});}
        private bool Due(string stamp)
        {DateTime parsed;return !DateTime.TryParse(stamp,CultureInfo.InvariantCulture,DateTimeStyles.RoundtripKind,out parsed)||parsed.ToUniversalTime()<=Now;}
        internal bool CheckDue(CloudJob job)
        {try{return Due(Load(job).NextCheckUtc);}catch{return false;}}
        private static bool PendingState(string state)
        {return new[]{"pending","source-busy","source-offline","permissions","sending","uncertain"}.Contains(state);}
        internal bool PendingNotification(CloudJob job)
        {try{return PendingState(Load(job).NotificationState);}catch{return false;}}
        internal bool NotificationDue(CloudJob job)
        {
            try{
                var value=Load(job);if(!PendingState(value.NotificationState))return false;
                var slot=store.ReadRecord<CloudJobSourceNoticeSlot>(SourceRecord(job));return Due(value.NotificationNextAttemptUtc)&&(slot.Binding!=SourceBinding(job)||Due(slot.NextAttemptUtc));
            }catch{return false;}
        }
        internal void BeginCheck(CloudJob job)
        {Edit(job,value=>{value.LastCheckedUtc=Now.ToString("o");value.NextCheckUtc=Now.AddSeconds(IntervalSeconds).ToString("o");});}
        internal bool SourceCheckDue(CloudJob job)
        {try{return Due(Load(job).SourceReturnNextCheckUtc);}catch{return false;}}
        internal void BeginSourceCheck(CloudJob job)
        {Edit(job,value=>{value.SourceReturnNextCheckUtc=Now.AddSeconds(IntervalSeconds).ToString("o");});}
        internal void SourceCheckError(CloudJob job,string error)
        {Edit(job,value=>{if(value.LastNotificationError!=error&&error!=null)Event(value,"source-check-error","Le traitement de l’orchestrateur ne peut pas être vérifié. Le résultat Cloud est conservé.");value.LastNotificationError=error;});}
        internal void CheckFinished(CloudJob job,string error)
        {Edit(job,value=>{if(value.LastCheckError!=error){if(error!=null)Event(value,"check-error","La vérification Cloud a échoué ; un nouvel essai est prévu.");else if(value.LastCheckError!=null)Event(value,"check-recovered","La connexion Cloud est rétablie.");}value.LastCheckError=error;if(error==null){value.LastSuccessfulCheckUtc=Now.ToString("o");value.LastKnownState=job.State;}});}
        internal bool BeginNotification(CloudJob job,bool scheduled)
        {
            using(store.Lease("cloud-notice-source-"+SourceBinding(job))){
                if(scheduled&&!NotificationDue(job))return false;
                var value=Load(job);if(!PendingState(value.NotificationState))return false;
                store.WriteRecord(SourceRecord(job),new CloudJobSourceNoticeSlot{Binding=SourceBinding(job),NextAttemptUtc=Now.AddSeconds(IntervalSeconds).ToString("o")});
                Edit(job,current=>{current.NotificationAttempts++;current.NotificationNextAttemptUtc=Now.AddSeconds(IntervalSeconds).ToString("o");});return true;
            }
        }
        internal string Marker(CloudJob job){return Load(job).NotificationMarker;}
        internal string Kind(CloudJob job){return Load(job).NotificationKind;}
        internal string NotificationState(CloudJob job){return Load(job).NotificationState;}
        internal void Notification(CloudJob job,string state,string error=null)
        {
            Edit(job,value=>{
                if(value.NotificationState!=state)Event(value,"notification-"+state,state=="delivered"?"Notification acceptée par le chat orchestrateur. Son traitement peut encore être en cours.":state=="sending"?"Envoi de la notification au chat orchestrateur.":state=="source-busy"?"L’orchestrateur attend une intervention ou travaille dans un moteur sans file de messages.":state=="source-offline"?"L’orchestrateur est indisponible ; le résultat est conservé et sera réessayé.":state=="permissions"?"Les permissions requises du chat orchestrateur ne sont pas confirmées. Le résultat est conservé.":state=="uncertain"?"L’envoi n’a pas été confirmé. Rapprochement en lecture seule ; aucun renvoi automatique.":"Notification à vérifier.");
                value.NotificationState=state;value.LastNotificationError=error;
                if(state=="delivered"){
                    value.NotificationSentUtc=Now.ToString("o");value.NotificationNextAttemptUtc=null;value.LastNotificationError=null;
                    if(value.NotificationKind=="intervention"&&!value.NotifiedInterventions.Contains(value.NotificationKey)){value.NotifiedInterventions.Add(value.NotificationKey);if(value.NotifiedInterventions.Count>32)value.NotifiedInterventions.RemoveAt(0);}
                }
            });
        }
    }
}
