using System;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal sealed class CloudTaskExecutionOptions
    {
        public string Model {get;set;}
        public string ReasoningEffort {get;set;}
        public string PermissionProfile {get;set;}
        internal bool Required {get{return !String.IsNullOrEmpty(Model)||!String.IsNullOrEmpty(ReasoningEffort)||!String.IsNullOrEmpty(PermissionProfile);}}
        internal static string Value(string value){return String.IsNullOrWhiteSpace(value)||value.Trim()=="inherit"?null:value.Trim();}
        internal static CloudTaskExecutionOptions Validate(CloudTaskExecutionOptions value)
        {
            value=value??new CloudTaskExecutionOptions();var copy=new CloudTaskExecutionOptions{Model=Value(value.Model),ReasoningEffort=Value(value.ReasoningEffort),PermissionProfile=Value(value.PermissionProfile)};
            if(copy.Model!=null&&!Regex.IsMatch(copy.Model,@"^[A-Za-z0-9][A-Za-z0-9._:-]{0,119}$"))throw new InvalidOperationException("Identifiant de modèle Cloud invalide.");
            if(copy.ReasoningEffort!=null&&!new[]{"none","minimal","low","medium","high","xhigh","max","ultra"}.Contains(copy.ReasoningEffort))throw new InvalidOperationException("Niveau de raisonnement Cloud invalide.");
            if(copy.PermissionProfile!=null&&!Regex.IsMatch(copy.PermissionProfile,@"^:?[a-z][a-z0-9-]{0,79}$"))throw new InvalidOperationException("Identifiant de profil de permissions Cloud invalide.");
            return copy;
        }
    }
    internal sealed class CloudTaskExecutionObservation
    {
        public string Model {get;set;}
        public string ReasoningEffort {get;set;}
        public string PermissionProfile {get;set;}
        public string ApprovalPolicy {get;set;}
        public string ApprovalsReviewer {get;set;}
        public bool Verified {get;set;}
        internal bool Matches(CloudTaskExecutionOptions requested)
        {
            return Verified&&(String.IsNullOrEmpty(requested.Model)||Model==requested.Model)&&(String.IsNullOrEmpty(requested.ReasoningEffort)||ReasoningEffort==requested.ReasoningEffort)&&(String.IsNullOrEmpty(requested.PermissionProfile)||PermissionProfile==requested.PermissionProfile);
        }
    }
    internal interface ICloudTasksExecutionConnection
    {
        Task<CloudTaskThread> Start(string configId,string cwd,CloudTaskExecutionOptions options,CancellationToken token);
        Task<CloudTaskTurn> StartTurn(string threadId,string prompt,string clientUserMessageId,CloudTaskExecutionOptions options,CancellationToken token);
        Task<CloudTaskExecutionObservation> ReadExecution(string threadId,CancellationToken token);
        Task<CloudTaskExecutionObservation> ConfigureExecution(string threadId,CloudTaskExecutionOptions options,CancellationToken token);
    }
    internal static class CloudMissionPreferences
    {
        internal const string Record="cloud-mission-preferences.dpapi";
        internal static CloudTaskExecutionOptions Read(RelayStore store)
        {return CloudTaskExecutionOptions.Validate(store.ReadRecord<CloudTaskExecutionOptions>(Record));}
        internal static void Save(RelayStore store,CloudTaskExecutionOptions options)
        {store.WriteRecord(Record,CloudTaskExecutionOptions.Validate(options));}
        internal static CloudTaskExecutionOptions Resolve(RelayStore store,CloudJobSpec spec,CloudTaskExecutionOptions frozen)
        {
            var inherited=frozen??Read(store);var explicitValues=CloudTaskExecutionOptions.Validate(new CloudTaskExecutionOptions{Model=spec.Model,ReasoningEffort=spec.ReasoningEffort,PermissionProfile=spec.PermissionProfile});
            return CloudTaskExecutionOptions.Validate(new CloudTaskExecutionOptions{
                Model=String.IsNullOrWhiteSpace(spec.Model)?inherited.Model:explicitValues.Model,
                ReasoningEffort=String.IsNullOrWhiteSpace(spec.ReasoningEffort)?inherited.ReasoningEffort:explicitValues.ReasoningEffort,
                PermissionProfile=String.IsNullOrWhiteSpace(spec.PermissionProfile)?inherited.PermissionProfile:explicitValues.PermissionProfile});
        }
    }
}
