using System;
using System.IO;
using System.Net;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Threading;

namespace MedQueue {
public class Slot {public string Id,PatientId,Start;}
public class Identity {public string Protocol,Practice,PracticeId;}
public class Receipt {public string EventId,Revision;}
public interface ICloud {Identity Verify();List<Slot> Schedule(DateTime now);Visit Chart(string id);Receipt Apply(Change e);}
public class Remote : ICloud {
 readonly string url,token;
 public Remote(string url,string token){
  Uri uri;if(!Uri.TryCreate(url,UriKind.Absolute,out uri)||uri.Scheme!="https"||uri.UserInfo!=""||uri.Query!=""||uri.Fragment!=""||String.IsNullOrWhiteSpace(token))throw new ArgumentException("Enter an HTTPS adapter URL and a credential.");
  this.url=uri.AbsoluteUri.TrimEnd('/');this.token=token;
 }
 T Send<T>(string path,object body){
  var r=(HttpWebRequest)WebRequest.Create(url+path);r.AllowAutoRedirect=false;r.Timeout=12000;r.ReadWriteTimeout=12000;r.Headers["Authorization"]="Bearer "+token;r.Accept="application/json";
  if(body!=null){r.Method="POST";r.ContentType="application/json";var b=Encoding.UTF8.GetBytes(Common.Json.Serialize(body));using(var w=r.GetRequestStream())w.Write(b,0,b.Length);}
  try{using(var response=(HttpWebResponse)r.GetResponse()){if(response.StatusCode!=HttpStatusCode.OK)throw new IOException("Unexpected adapter response.");using(var input=response.GetResponseStream())using(var buffer=new MemoryStream()){
   var chunk=new byte[8192];int n;while((n=input.Read(chunk,0,chunk.Length))>0){if(buffer.Length+n>8388608)throw new IOException("Adapter response exceeds 8 MB.");buffer.Write(chunk,0,n);}return Common.Json.Deserialize<T>(Encoding.UTF8.GetString(buffer.ToArray()));}}}
  catch(WebException ex){using(var resp=ex.Response as HttpWebResponse){if(resp!=null&&resp.StatusCode==HttpStatusCode.Conflict)throw new Conflict();}throw;}
 }
 public Identity Verify(){var i=Send<Identity>("/identity",null);if(i.Protocol!="medqueue/1"||String.IsNullOrWhiteSpace(i.PracticeId)||String.IsNullOrWhiteSpace(i.Practice))throw new IOException("Incompatible adapter identity.");return i;}
 public List<Slot> Schedule(DateTime n){return Send<List<Slot>>("/appointments?from="+Uri.EscapeDataString(n.ToString("o"))+"&until="+Uri.EscapeDataString(n.AddHours(24).ToString("o")),null);}
 public Visit Chart(string id){return Send<Visit>("/visits/"+Uri.EscapeDataString(id),null);}
 public Receipt Apply(Change c){return Send<Receipt>("/changes",c);}
}
public class Conflict:Exception {}
public class Dummy : ICloud {
 readonly Vault db;public Dummy(Vault db){this.db=db;}
 public Identity Verify(){return new Identity{Practice="MedQueue training clinic",PracticeId="demo",Protocol="medqueue/1"};}
 public void Seed(){db.Save(s=>{if(s.Visits.Count>0)return;int i=0;foreach(string name in new[]{"Avery Sample","Jordan Example","Taylor Fictional"}){i++;s.Visits.Add(new Visit{Id="demo-"+i,PatientId="sample-"+i,Name=name,Start=DateTime.UtcNow.AddHours(i==3?26:i).ToString("o"),Revision="1",Records=new List<Record>{new Record{Id=Common.Id(),Kind="History",Title="Training chart",Body="Fictional patient for outage drills."}}});}});}
 public List<Slot> Schedule(DateTime n){return db.View(s=>s.Visits.Where(v=>Common.Eligible(v.Start,n)).Select(v=>new Slot{Id=v.Id,PatientId=v.PatientId,Start=v.Start}).ToList());}
 public Visit Chart(string id){return db.View(s=>s.Visits.Single(v=>v.Id==id));}
 public Receipt Apply(Change e){Receipt result=null;db.Save(s=>{var prior=s.Log.Find(a=>a.Action==e.Id);if(prior!=null){result=new Receipt{EventId=e.Id,Revision=prior.Target};return;}
 var v=s.Visits.Single(x=>x.Id==e.VisitId&&x.PatientId==e.PatientId);if(v.Revision!=e.BaseRevision)throw new Conflict();
 if(e.Kind=="check-in")v.CheckedIn=e.At;else{v.Records.RemoveAll(r=>r.Id==e.Record.Id);v.Records.Add(e.Record);}
 v.Revision=Common.Id();s.Audit(e.Actor,e.Id,v.Revision);result=new Receipt{EventId=e.Id,Revision=v.Revision};});return result;}
}
public class Sync {
 readonly Vault local;public Sync(Vault v){local=v;}
 public void Run(ICloud api,string practice){
 if(api.Verify().PracticeId!=practice)throw new IOException("Adapter practice identity changed.");
 var pending=local.View(s=>s.Queue.ToList());var held=new HashSet<string>();
 foreach(var item in pending){
  if(held.Contains(item.VisitId))continue;
  var e=local.View(s=>s.Queue.Find(x=>x.Id==item.Id));if(e==null)continue;
  if(e.Conflict){held.Add(e.VisitId);continue;}
  try{var ack=api.Apply(e);if(ack.EventId!=e.Id||String.IsNullOrEmpty(ack.Revision))throw new IOException("Invalid acknowledgement.");
   local.Save(s=>{var v=s.Visits.Single(x=>x.Id==e.VisitId);v.Revision=ack.Revision;s.Queue.RemoveAll(x=>x.Id==e.Id);var next=s.Queue.Find(x=>x.VisitId==e.VisitId);if(next!=null)next.BaseRevision=ack.Revision;s.Audit("sync","acknowledged",e.Id);});}
  catch(Conflict){held.Add(e.VisitId);local.Save(s=>{s.Queue.Single(x=>x.Id==e.Id).Conflict=true;s.Audit("sync","revision conflict",e.VisitId);});}
 }
 var now=DateTime.UtcNow;var slots=api.Schedule(now).Where(x=>Common.Eligible(x.Start,now)).ToList();
 foreach(var slot in slots){
 if(local.View(s=>s.Queue.Exists(e=>e.VisitId==slot.Id)))continue;
 var chart=api.Chart(slot.Id);
 if(chart.Id!=slot.Id||chart.PatientId!=slot.PatientId||Common.Utc(chart.Start)!=Common.Utc(slot.Start)||String.IsNullOrWhiteSpace(chart.Name)||String.IsNullOrWhiteSpace(chart.Revision)||chart.Records==null||chart.Records.Any(r=>r==null||String.IsNullOrEmpty(r.Id))||chart.Records.Select(r=>r.Id).Distinct().Count()!=chart.Records.Count)throw new IOException("Inconsistent chart from adapter.");
 chart.PassHash=null;chart.PassUntil=null;chart.Active=false;
 local.Save(s=>{if(s.Queue.Exists(e=>e.VisitId==slot.Id))return;var old=s.Visits.Find(v=>v.Id==slot.Id);if(old!=null){if(old.PatientId!=chart.PatientId)throw new IOException("Visit identity changed.");chart.PassHash=old.PassHash;chart.PassUntil=old.PassUntil;chart.Active=old.Active;s.Visits.Remove(old);}s.Visits.Add(chart);});
 }
 local.Save(s=>{s.Visits.RemoveAll(v=>!s.Queue.Exists(e=>e.VisitId==v.Id)&&!v.Active&&!slots.Exists(x=>x.Id==v.Id)&&(Common.Utc(v.Start)>now||Common.Utc(v.Start)<now.AddHours(-24)));s.LastSync=Common.Now();});
 }
}
}
