using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Creezio.Switcher;

internal static class WebhookHttpAuthTests
{
    private const string Body="{\"title\":\"Fixture HTTP\",\"prompt\":\"Fictional local receipt only.\"}";
    private static void Assert(bool value,string reason){if(!value)throw new Exception(reason);}
    private static void Reject(int status,Action action){try{action();}catch(WebhookException e){Assert(e.StatusCode==status,"unexpected status "+e.StatusCode);return;}throw new Exception("expected HTTP rejection "+status);}
    private sealed class ObservedStream:MemoryStream
    {
        private readonly long headerLength;private readonly bool rejectBody,timeout;
        internal int BodyReads,BeforeBodyCalls;internal readonly List<int> Timeouts=new List<int>();
        internal ObservedStream(string headers,string body,bool rejectBody,bool timeout=false):base(Encoding.UTF8.GetBytes(headers+body)){headerLength=Encoding.UTF8.GetByteCount(headers);this.rejectBody=rejectBody;this.timeout=timeout;}
        public override bool CanTimeout{get{return timeout;}}
        public override int ReadTimeout{get{return Timeouts.Count==0?10000:Timeouts[Timeouts.Count-1];}set{Timeouts.Add(value);}}
        private void ReadBody(){if(Position<headerLength)return;BodyReads++;if(rejectBody)throw new Exception("Unauthorized request attempted a body read");}
        public override int Read(byte[] buffer,int offset,int count){if(count>0)ReadBody();return base.Read(buffer,offset,count);}
        public override int ReadByte(){ReadBody();if(timeout&&Position==20)Thread.Sleep(25);return base.ReadByte();}
    }
    private sealed class Fixture
    {
        internal readonly RelayStore Store;internal WebhookRoute Route;internal string Key;
        internal Fixture(string root,string name)
        {
            string folder=Path.Combine(root,"http-header-auth-tests",name),home=Path.Combine(folder,"home"),workspace=Path.Combine(folder,"workspace");Directory.CreateDirectory(home);Directory.CreateDirectory(workspace);Store=new RelayStore(Path.Combine(folder,"relay"));
            Webhooks.SaveConfig(Store,new WebhookConfig{Enabled=true,Port=47831});Route=Webhooks.Routes(Store).SingleOrDefault(route=>route.Slug=="fixture-hook")??new WebhookRoute{Name="HTTP fixture",Slug="fixture-hook",InstanceId=new string('a',32),AccountKey="fake-http-account",Home=home,Workspace=workspace,Enabled=true};Route.Enabled=true;Route=Webhooks.SaveRoute(Store,Route);Key=Webhooks.RotateKey(Store,Route.Id);Route=Webhooks.Route(Store,Route.Id);
        }
        internal string Headers(string bearer,string extra="",string path="/v1/hooks/fixture-hook/jobs")
        {return "POST "+path+" HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: "+Encoding.UTF8.GetByteCount(Body)+"\r\n"+(bearer==null?"":"Authorization: Bearer "+bearer+"\r\n")+extra+"\r\n";}
        internal WebhookHttpRequest Parse(ObservedStream stream){return WebhookHttp.ReadRequest(stream,request=>{stream.BeforeBodyCalls++;Assert(stream.BodyReads==0,"authentication happened after reading body");WebhookHttp.ValidateHeaders(Store,request);});}
    }
    internal static void RunAll(Action<string,Action> check,string root)
    {
        check("webhook HTTP missing authorization is rejected before any body read",()=>{
            var f=new Fixture(root,"missing");using(var stream=new ObservedStream(f.Headers(null),Body,true)){Reject(401,()=>f.Parse(stream));Assert(stream.BodyReads==0&&stream.BeforeBodyCalls==1,"unauthorized body was consumed");}Assert(Webhooks.Jobs(f.Store).Length==0,"unauthorized request created a job");
        });
        check("webhook HTTP invalid token and missing route are rejected before body",()=>{
            var f=new Fixture(root,"wrong-token");foreach(string path in new[]{"/v1/hooks/fixture-hook/jobs","/v1/hooks/nonexistent/jobs"})using(var stream=new ObservedStream(f.Headers("whk_"+new string('x',43),"",path),Body,true)){Reject(401,()=>f.Parse(stream));Assert(stream.BodyReads==0,"invalid token reached body read");}
        });
        check("webhook HTTP valid headers authorize before reading and decoding JSON",()=>{
            var f=new Fixture(root,"valid");using(var stream=new ObservedStream(f.Headers(f.Key),Body,false)){var request=f.Parse(stream);Assert(request.Body==Body&&stream.BodyReads>0&&stream.BeforeBodyCalls==1,"authorized body was not read after preflight");}Assert(Webhooks.Jobs(f.Store).Length==0,"header validation itself admitted a job");
        });
        check("webhook HTTP duplicate authorization is rejected before authorization or body",()=>{
            var f=new Fixture(root,"duplicate-auth");using(var stream=new ObservedStream(f.Headers(f.Key,"authorization: Bearer wrong\r\n"),Body,true)){Reject(400,()=>f.Parse(stream));Assert(stream.BeforeBodyCalls==0&&stream.BodyReads==0,"ambiguous auth reached preflight/body");}
        });
        check("webhook HTTP ambiguous framing cannot bypass header authentication",()=>{
            var f=new Fixture(root,"ambiguous-framing");foreach(string extra in new[]{"Content-Length: 1\r\n","Transfer-Encoding: chunked\r\n","Expect: 100-continue\r\n","Upgrade: websocket\r\n"," Authorization: Bearer hidden\r\n"})using(var stream=new ObservedStream(f.Headers(f.Key,extra),Body,true)){Reject(400,()=>f.Parse(stream));Assert(stream.BodyReads==0&&stream.BeforeBodyCalls==0,"ambiguous framing consumed body or reached auth");}
        });
        check("webhook HTTP query encoded and malformed routes cannot bypass authentication",()=>{
            var f=new Fixture(root,"routes");foreach(string path in new[]{"/v1/hooks/fixture-hook/jobs?key=private","/v1/hooks/fixture%2dhook/jobs","/v1/hooks/fixture-hook/jobs#secret"})using(var stream=new ObservedStream(f.Headers(f.Key,"",path),Body,true)){Reject(400,()=>f.Parse(stream));Assert(stream.BodyReads==0,"malformed route consumed body");}
            using(var stream=new ObservedStream(f.Headers(f.Key,"","/unknown"),Body,true)){Reject(404,()=>f.Parse(stream));Assert(stream.BodyReads==0,"unknown route consumed body");}
        });
        check("webhook HTTP disabled destination rejects before reading an upload",()=>{
            var f=new Fixture(root,"disabled");f.Route.Enabled=false;f.Route=Webhooks.SaveRoute(f.Store,f.Route);using(var stream=new ObservedStream(f.Headers(f.Key),Body,true)){Reject(503,()=>f.Parse(stream));Assert(stream.BodyReads==0,"disabled route waited for request body");}
        });
        check("webhook HTTP revoked key is refused before reading body",()=>{
            var f=new Fixture(root,"revoked");string previous=f.Key;f.Key=Webhooks.RotateKey(f.Store,f.Route.Id);using(var stream=new ObservedStream(f.Headers(previous),Body,true)){Reject(401,()=>f.Parse(stream));Assert(stream.BodyReads==0,"revoked key read an upload");}
        });
        check("webhook HTTP health stays generic without requiring a route key",()=>{
            var f=new Fixture(root,"health");using(var stream=new ObservedStream("GET /health HTTP/1.1\r\nHost: localhost\r\n\r\n","",true)){var request=f.Parse(stream);var response=WebhookHttp.Handle(f.Store,request);Assert(response.Status==200&&response.Body=="{\"status\":\"ok\"}"&&stream.BodyReads==0,"health exposed metadata or waited for body");}
        });
        check("webhook HTTP pure parser remains available without store authorization",()=>{
            var f=new Fixture(root,"pure");using(var stream=new ObservedStream(f.Headers(null),Body,false)){var request=WebhookHttp.ReadRequest(stream);Assert(request.Body==Body&&stream.BodyReads>0&&stream.BeforeBodyCalls==0,"pure parser compatibility changed");}
        });
        check("webhook HTTP authorization is repeated at admission after key rotation during body read",()=>{
            var f=new Fixture(root,"recheck");WebhookHttpRequest request;using(var stream=new ObservedStream(f.Headers(f.Key,"Idempotency-Key: auth-recheck\r\n"),Body,false))request=f.Parse(stream);Webhooks.RotateKey(f.Store,f.Route.Id);var response=WebhookHttp.Handle(f.Store,request);Assert(response.Status==401&&Webhooks.Jobs(f.Store).Length==0,"stale preflight bypassed current authorization");
        });
        check("webhook HTTP blocking reads share the initial absolute request deadline",()=>{
            var f=new Fixture(root,"deadline");using(var stream=new ObservedStream(f.Headers(f.Key),Body,false,true)){f.Parse(stream);Assert(stream.Timeouts.Count>1&&stream.Timeouts[0]<=10000&&stream.Timeouts.Last()<stream.Timeouts[0]-10,"receive timeout was reset instead of using the initial deadline");}
        });
    }
}
