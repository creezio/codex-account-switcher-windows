using System;
using Creezio.Switcher;

internal static class PortalComposerOptionsTests
{
    private static void Assert(bool value,string text){if(!value)throw new Exception(text);}
    private static void Reject(Action action){try{action();}catch(WebhookException e){Assert(e.StatusCode==400,"Expected a malformed option rejection");return;}throw new Exception("Invalid composer option was accepted");}
    internal static void RunAll(Action<string,Action> check,string root)
    {
        check("composer parsing retains legacy retry fingerprints",()=>{
            var plain=Webhooks.ParseBody("{\"prompt\":\"Hello\"}");
            var empty=Webhooks.ParseBody("{\"prompt\":\"Hello\",\"options\":{}}");
            Assert(Webhooks.PayloadFingerprint(plain)==Webhooks.Hash(Json.Write(new[]{"Mission externe","Hello",""})),"Legacy idempotency changed");
            Assert(Webhooks.PayloadFingerprint(empty)==Webhooks.PayloadFingerprint(plain),"Empty options changed a retry");
        });
        check("composer parses structured selectors without local paths or arbitrary policies",()=>{
            string attachment=new string('a',32),skill=new string('b',64);
            var payload=Webhooks.ParseBody("{\"prompt\":\"Inspect\",\"options\":{\"model\":\"gpt-6.1-sol\",\"reasoningEffort\":\"ultra\",\"collaborationMode\":\"plan\",\"plugins\":[\"app_123\"],\"skills\":[\""+skill+"\"],\"attachments\":[\""+attachment+"\"]}}");
            var options=PortalMessageOptions.FromPayload(payload);Assert(options.Model=="gpt-6.1-sol"&&options.CollaborationMode=="plan"&&options.Attachments[0]==attachment&&options.Skills[0]==skill,"Lost structured selections");
        });
        check("composer selectors participate in durable idempotency",()=>{
            Func<string,string> hash=model=>Webhooks.PayloadFingerprint(Webhooks.ParseBody("{\"prompt\":\"Same\",\"options\":{\"model\":\""+model+"\"}}"));
            Assert(hash("gpt-6.1-sol")!=hash("gpt-6-astra"),"A changed model can silently reuse a prior send");
            var first=Webhooks.ParseBody("{\"prompt\":\"Same\",\"options\":{\"plugins\":[\"two\",\"one\"]}}");
            var second=Webhooks.ParseBody("{\"prompt\":\"Same\",\"options\":{\"plugins\":[\"one\",\"two\"]}}");
            Assert(Webhooks.PayloadFingerprint(first)==Webhooks.PayloadFingerprint(second),"Unordered plugin set changes retry identity");
        });
        check("composer rejects duplicate escaped keys, foreign paths and arbitrary policies",()=>{
            foreach(string options in new[]{"null","[]","{\"model\":\"a\",\"mo\\u0064el\":\"b\"}","{\"permissionProfile\":\"unrestricted\"}","{\"sandboxPolicy\":{}}","{\"skills\":[\"C:/secret/SKILL.md\"]}","{\"attachments\":[\"../secret\"]}","{\"plugins\":[\"duplicate\",\"duplicate\"]}","{\"collaborationMode\":\"anything\"}","{\"reasoningEffort\":\"unlimited\"}","{\"model\":\"bad model\"}","{\"plugins\":{}}","{\"plugins\":[true]}","{\"plugins\":[\"app\",]}","{\"plugins\":[\"app\"]} junk"})Reject(()=>Webhooks.ParseBody("{\"prompt\":\"Safe\",\"options\":"+options+"}"));
            Reject(()=>Webhooks.ParseBody("{\"prompt\":\"Safe\",\"options\":{},\"opt\\u0069ons\":{}}"));
        });
        check("composer bounds selectors and preserves ordered attachment context",()=>{
            string a=new string('a',32),b=new string('b',32);var first=Webhooks.ParseBody("{\"prompt\":\"Same\",\"options\":{\"attachments\":[\""+a+"\",\""+b+"\"]}}");var second=Webhooks.ParseBody("{\"prompt\":\"Same\",\"options\":{\"attachments\":[\""+b+"\",\""+a+"\"]}}");Assert(Webhooks.PayloadFingerprint(first)!=Webhooks.PayloadFingerprint(second),"Attachment ordering was lost");
            Reject(()=>Webhooks.ParseBody("{\"prompt\":\"Safe\",\"options\":{\"plugins\":[\""+new string('a',301)+"\"]}}"));
        });
    }
}
