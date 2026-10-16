using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Creezio.Switcher
{
    internal static class CloudCommands
    {
        internal static async Task<CloudConfiguration> Create(AccountService accounts,DesktopInstance instance,CloudConfigurationCreate request,CancellationToken token)
        {return await Create(accounts,instance,request,Guid.NewGuid().ToString("N"),token);}
        internal static async Task<CloudConfiguration> Create(AccountService accounts,DesktopInstance instance,CloudConfigurationCreate request,string operationId,CancellationToken token)
        {
            using(var client=CloudInstanceConnection.Open(accounts,instance))
                return await CloudMutationJournal.Run(accounts.Vault.Root,operationId,"create",instance.Id,instance.AccountKey,request,()=>client.Create(request,token),id=>client.Get(id,token));
        }
        internal static async Task<CloudConfiguration> Rename(AccountService accounts,DesktopInstance instance,string id,string name,CancellationToken token)
        {return await Rename(accounts,instance,id,name,Guid.NewGuid().ToString("N"),token);}
        internal static async Task<CloudConfiguration> Rename(AccountService accounts,DesktopInstance instance,string id,string name,string operationId,CancellationToken token)
        {
            using(var client=CloudInstanceConnection.Open(accounts,instance))
                return await CloudMutationJournal.Run(accounts.Vault.Root,operationId,"rename",instance.Id,instance.AccountKey,new{id=id,name=name},()=>client.Rename(id,name,token),configurationId=>client.Get(configurationId,token));
        }
        private static string Required(object input,string key)
        {string value=Json.Str(Json.Get(input,key));if(String.IsNullOrWhiteSpace(value))throw new InvalidOperationException("Champ Cloud requis : "+key);return value;}
        internal static async Task<object> Execute(RelayStore relay,object input,CancellationToken token)
        {
            var accounts=new AccountService(Path.GetDirectoryName(relay.Root));
            string operation=Required(input,"operation");
            var jobs=new CloudJobs(relay);
            if(operation=="cloud-jobs")return jobs.List().Select(job=>CloudJobs.Summary(job,false)).ToArray();
            if(operation=="cloud-job-read")return CloudJobs.Summary(jobs.Get(Required(input,"id")),true);
            if(operation=="cloud-job-poll")return CloudJobs.Summary(await jobs.Advance(Required(input,"id"),token),true);
            if(operation=="cloud-job-cancel")return CloudJobs.Summary(jobs.CancelQueued(Required(input,"id")),false);
            if(operation=="cloud-job-submit"){
                string sessionToken=Json.Str(Json.Get(input,"session"));var source=String.IsNullOrEmpty(sessionToken)?null:RelaySessions.Resolve(relay,sessionToken,CodexEnvironment.DefaultHome());
                var admitted=await jobs.Submit(Json.Read<CloudJobSpec>(Json.Write(Json.Get(input,"job"))),source,token);
                if(!Object.Equals(Json.Get(input,"startWorker"),false))CloudJobWorker.Ensure(relay);
                return CloudJobs.Summary(admitted,true);
            }
            var instance=NamedInstances.Resolve(accounts,Required(input,"instance"));
            if(operation=="cloud-github-repositories"||operation=="cloud-github-repository"||operation=="cloud-github-status"){
                using(var github=CloudInstanceConnection.OpenGitHub(accounts,instance)){
                    if(operation=="cloud-github-status")return await github.ListInstallations(token);
                    if(operation=="cloud-github-repository")return await github.GetRepository(Required(input,"repositoryId"),token);
                    return await github.ListRepositories((int)(Json.Number(Json.Get(input,"page"))??1),token);
                }
            }
            if(operation=="cloud-setup-start")return await CloudEnvironmentSetup.Start(accounts,instance,Required(input,"configurationId"),Json.Read<System.Collections.Generic.Dictionary<string,string>>(Json.Write(Json.Get(input,"repositoryNames"))),Required(input,"id"),token);
            if(operation=="cloud-setup-follow")return await CloudEnvironmentSetup.Follow(accounts,instance,Required(input,"id"),token);
            if(operation=="cloud-setup-publish")return await CloudEnvironmentSetup.Publish(accounts,instance,Required(input,"id"),token);
            if(operation=="cloud-task-list"||operation=="cloud-task-read"||operation=="cloud-task-tools"){
                using(var client=CloudInstanceConnection.OpenTasks(accounts,instance)){
                    if(operation=="cloud-task-tools")return await ((ICloudTasksInventoryConnection)client).InventoryTools(Required(input,"threadId"),token);
                    if(operation=="cloud-task-read")return await client.Read(Required(input,"threadId"),token);
                    string cursor=Json.Str(Json.Get(input,"cursor"));return await client.List(String.IsNullOrEmpty(cursor)?null:cursor,10,token);
                }
            }
            if(operation=="cloud-update")return await CloudEnvironmentUpdate.Update(accounts,instance,Required(input,"configurationId"),Json.Read<CloudDraftPatch>(Json.Write(Json.Get(input,"patch"))),Required(input,"id"),token);
            if(operation=="cloud-follow")return await CloudEnvironmentUpdate.Follow(accounts,instance,Required(input,"id"),token);
            if(operation=="cloud-create")return await Create(accounts,instance,Json.Read<CloudConfigurationCreate>(Json.Write(Json.Get(input,"configuration"))),Required(input,"id"),token);
            if(operation=="cloud-rename")return await Rename(accounts,instance,Required(input,"configurationId"),Required(input,"name"),Required(input,"id"),token);
            using(var client=CloudInstanceConnection.Open(accounts,instance)){
                if(operation=="cloud-list"){string cursor=Json.Str(Json.Get(input,"cursor"));return await client.List("user",String.IsNullOrEmpty(cursor)?null:cursor,token);}
                if(operation=="cloud-read")return await client.Get(Required(input,"configurationId"),token);
            }
            throw new InvalidOperationException("Commande Cloud inconnue.");
        }
    }
}
