using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace Creezio.Switcher
{
    internal static class PortalAdministrationHttp
    {
        private static readonly ConcurrentDictionary<string,Lazy<PortalEnrollmentService>> Services=new ConcurrentDictionary<string,Lazy<PortalEnrollmentService>>(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string,Lazy<PortalPluginService>> PluginServices=new ConcurrentDictionary<string,Lazy<PortalPluginService>>(StringComparer.OrdinalIgnoreCase);
        private static PortalPluginService Plugins(RelayStore store){return PluginServices.GetOrAdd(store.Root,ignored=>new Lazy<PortalPluginService>(()=>new PortalPluginService(store))).Value;}
        private static PortalEnrollmentService Service(RelayStore store)
        {return Services.GetOrAdd(store.Root,ignored=>new Lazy<PortalEnrollmentService>(()=>new PortalEnrollmentService(store,(route,actor)=>PortalAdministration.RequireActor(store,route,actor)))).Value;}
        internal static void Tick(RelayStore store)
        {Lazy<PortalEnrollmentService> existing;if(Services.TryGetValue(store.Root,out existing)){if(existing.IsValueCreated)existing.Value.Tick();}else if(PortalAdministration.Policy(store).Enabled)Service(store).Tick();PortalScheduleWorker.Ensure(store);}
        internal static void Stop(RelayStore store){Lazy<PortalEnrollmentService> service;if(Services.TryRemove(store.Root,out service)&&service.IsValueCreated)service.Value.Dispose();Lazy<PortalPluginService> plugins;if(PluginServices.TryRemove(store.Root,out plugins)&&plugins.IsValueCreated)plugins.Value.Dispose();}
        private static WebhookHttpResponse Result(int status,object value){return new WebhookHttpResponse{Status=status,Body=Json.Write(value)};}
        private static WebhookHttpResponse Error(int status,string message){return Result(status,new{error=message});}
        internal static WebhookHttpResponse Handle(RelayStore store,WebhookHttpRequest request)
        {return Handle(store,request,null);}
        internal static WebhookHttpResponse Handle(RelayStore store,WebhookHttpRequest request,PortalEnrollmentService fixtureService,PortalPluginService fixturePlugins=null)
        {
            try{
                var identity=PortalAdministration.Authorize(store,request);var path=PortalAdministration.Path(request.Path);
                using(request.Method=="POST"?ApplicationUpdates.AdmitPortalMutation(store):null){
                if(PortalResourceAccessHttp.Path(request.Path).Success)return PortalResourceAccessHttp.Handle(store,request,identity);
                if(PortalManagementHttp.Path(request.Path).Success)return PortalManagementHttp.Handle(store,request,identity);
                if(path.Groups[5].Success){
                    var plugins=fixturePlugins??Plugins(store);string instance=path.Groups[5].Value,kind=path.Groups[6].Value,operation=path.Groups[7].Value;
                    if(request.Method=="GET"&&kind=="plugins"&&operation.Length==0)using(var timeout=new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60)))return Result(200,plugins.Catalog(identity.Route,identity.Actor,instance,timeout.Token));
                    if(request.Method=="GET"&&kind=="plugin-installs"&&operation.Length>0)return Result(200,plugins.Read(identity.Route,identity.Actor,instance,operation));
                    if(request.Method=="POST"&&kind=="plugin-installs"&&operation.Length==0){var fields=FlatObject.Parse(request.Body,new[]{"pluginId"});object value;string plugin=fields.TryGetValue("pluginId",out value)?value as string:null;if(fields.Count!=1||!Regex.IsMatch(plugin??"",@"\A[a-f0-9]{64}\z"))throw new WebhookException(400,"Choisissez un plugin du catalogue de cette instance.");string idempotency;request.Headers.TryGetValue("Idempotency-Key",out idempotency);return Result(202,plugins.Start(identity.Route,identity.Actor,instance,plugin,idempotency));}
                    return Error(405,"Methode non autorisee.");
                }
                string collection=path.Groups[2].Value,id=path.Groups[3].Value,action=path.Groups[4].Value;
                var service=fixtureService??Service(store);
                if(collection=="capabilities")return request.Method=="GET"&&id.Length==0?Result(200,service.Capabilities(identity.Route,identity.Actor)):Error(405,"Methode non autorisee.");
                if(request.Method=="GET"&&id.Length>0&&action.Length==0)return Result(200,service.Get(identity.Route,identity.Actor,id));
                if(request.Method!="POST")return Error(405,"Methode non autorisee.");
                string key;request.Headers.TryGetValue("Idempotency-Key",out key);
                if(id.Length==0){
                    var body=FlatObject.Parse(request.Body,new[]{"name","accountId","exposeInPortal","loginMethod"});object raw;string name=body.TryGetValue("name",out raw)?raw as string:null;
                    string account=body.TryGetValue("accountId",out raw)?raw as string:null;
                    string method=body.TryGetValue("loginMethod",out raw)?raw as string:"device";
                    if(String.IsNullOrWhiteSpace(name)||name.Trim().Length>70||name.Any(Char.IsControl)||body.ContainsKey("accountId")&&body["accountId"]!=null&&!(body["accountId"] is string)||account!=null&&!Regex.IsMatch(account,@"\A[a-f0-9]{64}\z")||!body.ContainsKey("exposeInPortal")||!(body["exposeInPortal"] is bool)||(method!="device"&&method!="browser"))throw new WebhookException(400,"Indiquez un nom de 1 a 70 caracteres, un compte valide, le choix d'acces au portail et une methode de connexion reconnue.");
                    return Result(202,service.Start(identity.Route,identity.Actor,key,new EnrollmentRequest{Name=name.Trim(),AccountId=account,ExposeInPortal=(bool)body["exposeInPortal"],LoginMethod=method}));
                }
                if(action=="browser-callback"){
                    var body=FlatObject.Parse(request.Body,new[]{"callbackUrl"});object raw;string callback=body.TryGetValue("callbackUrl",out raw)?raw as string:null;
                    if(body.Count!=1||String.IsNullOrEmpty(callback)||callback.Length>4096)throw new WebhookException(400,"Collez l'adresse de retour de cette connexion OpenAI.");
                    return Result(202,service.BrowserCallback(identity.Route,identity.Actor,id,callback));
                }
                if(action=="confirm"){
                    var body=FlatObject.Parse(request.Body,new[]{"identityHash"});object raw;string hash=body.TryGetValue("identityHash",out raw)?raw as string:null;
                    if(body.Count!=1||!Regex.IsMatch(hash??"",@"\A[a-f0-9]{64}\z"))throw new WebhookException(400,"Confirmez l'identite effectivement connectee avant de creer l'instance.");
                    return Result(202,service.Confirm(identity.Route,identity.Actor,id,hash));
                }
                if(action=="cancel"){
                    if(FlatObject.Parse(request.Body,new string[0]).Count!=0)throw new WebhookException(400,"L'annulation ne demande aucun argument.");
                    return Result(200,service.Cancel(identity.Route,identity.Actor,id));
                }
                return Error(405,"Methode non autorisee.");
                }
            }catch(WebhookException error){return Error(error.StatusCode,error.Message);}
            catch(System.OperationCanceledException){return Error(504,"La demande a expire. Consultez sa progression avant de reessayer.");}
            catch(System.IO.IOException){return Error(409,"Le stockage local est occupe ou indisponible. Reprenez cette meme operation sans en creer une autre.");}
            catch(InvalidOperationException){return Error(409,"Cette operation n'est plus disponible. Actualisez son etat ; aucune autre instance n'a ete modifiee.");}
        }
        // A flat bounded grammar, with decoded duplicate-key detection on both
        // Framework and .NET. No credentials, paths or nested policy accepted.
        internal static class FlatObject
        {
            internal static Dictionary<string,object> Parse(string text,string[] allowed){return new Reader(text,allowed).Read();}
            private sealed class Reader
            {
                private readonly string text;private readonly string[] allowed;private int at;
                internal Reader(string text,string[] allowed){this.text=text??"";this.allowed=allowed;if(this.text.Length>PortalAdministration.MaximumBody)throw Bad();}
                private static WebhookException Bad(){return new WebhookException(400,"Corps JSON d'administration invalide : utilisez uniquement les champs attendus, sans doublons.");}
                private void Space(){while(at<text.Length&&" \r\n\t".IndexOf(text[at])>=0)at++;}
                private bool Peek(char value){Space();return at<text.Length&&text[at]==value;}
                private void Take(char value){Space();if(at>=text.Length||text[at++]!=value)throw Bad();}
                private string String(){Space();int start=at;Take('"');bool end=false;while(at<text.Length){char value=text[at++];if(value=='"'){end=true;break;}if(value<32)throw Bad();if(value=='\\'){if(at>=text.Length)throw Bad();char escape=text[at++];if(escape=='u'){for(int index=0;index<4;index++)if(at>=text.Length||!Uri.IsHexDigit(text[at++]))throw Bad();}else if("\"\\/bfnrt".IndexOf(escape)<0)throw Bad();}}if(!end)throw Bad();try{return Json.Read<string>(text.Substring(start,at-start));}catch(Exception){throw Bad();}}
                private object Value(){Space();if(Peek('"'))return String();foreach(string literal in new[]{"true","false","null"})if(at+literal.Length<=text.Length&&text.Substring(at,literal.Length)==literal){at+=literal.Length;return literal=="null"?null:(object)(literal=="true");}throw Bad();}
                internal Dictionary<string,object> Read(){var result=new Dictionary<string,object>(StringComparer.Ordinal);Take('{');if(Peek('}'))at++;else while(true){string key=String();if(!allowed.Contains(key)||result.ContainsKey(key))throw Bad();Take(':');result.Add(key,Value());Space();if(at>=text.Length)throw Bad();char next=text[at++];if(next=='}')break;if(next!=',')throw Bad();}Space();if(at!=text.Length)throw Bad();return result;}
            }
        }
    }
}
