using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using MedQueue;

class Smoke {
 static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
 static void Main(){
  CheckSplitConnections();
  Check(Common.IsNewPasswordValid("PracticeAdmin9!"),"Valid password rejected.");
  Check(!Common.IsNewPasswordValid("practiceadmin9!"),"Password missing a capital letter accepted.");
  Check(!Common.IsNewPasswordValid("PracticeAdminA!"),"Password missing a number accepted.");
  Check(!Common.IsNewPasswordValid("PracticeAdmin99"),"Password missing a symbol accepted.");
  Check(!Common.IsNewPasswordValid("PracticeAdmin9!<"),"Unsupported punctuation accepted.");
  Check(!Common.IsNewPasswordValid("Admin9!")&&!Common.IsNewPasswordValid(new String('A',254)+"a9!"),"Password length limits failed.");
  string root=Path.Combine(Environment.CurrentDirectory,"dist","test-"+Common.Id());
  Directory.CreateDirectory(Path.Combine(root,"local"));Directory.CreateDirectory(Path.Combine(root,"cloud"));
  string localPath=Path.Combine(root,"local");string visitId,eventId;
  using(var cloud=new Vault(Path.Combine(root,"cloud"))){
   var dummy=new Dummy(cloud);dummy.Seed();
   using(var vault=new Vault(localPath)){
    ProviderAccounts.Create(vault,null,"training-admin","PracticeAdmin9!",true);
    new Sync(vault).Run(dummy,"demo");
    Check(vault.View(s=>s.Visits.Count)==2,"Only the next 24 hours should load.");
    Check(vault.View(s=>PatientIdentity.Find(s,"provider","Avery Sample","1988-04-13","202-555-0110").Count)==0,"Wrong birth date revealed a patient.");
    Check(vault.View(s=>PatientIdentity.Find(s,"provider","Avery Sample","1988-04-12","202-555-0111").Count)==0,"Wrong phone revealed a patient.");
    Check(vault.View(s=>PatientIdentity.Find(s,"provider","Jordan Example","1988-04-12","202-555-0110").Count)==0,"Wrong name revealed a patient.");
    Check(vault.View(s=>PatientIdentity.Find(s,"provider","  avery  sample ","1988-04-12","(202) 555-0110").Count)==1,"Verified patient did not match.");
    visitId=vault.View(s=>s.Visits.First().Id);
    vault.Save(s=>{
     var visit=s.Visits.Single(x=>x.Id==visitId);
     s.CheckIn(visit,"provider");
     s.CheckIn(visit,"provider");
     s.Edit(visit,"provider","prescription",new Record{Id="sample-rx",Kind="Prescription",Title="Training medication",Body="Fictional instructions"});
    });
    eventId=vault.View(s=>s.Queue.First().Id);
   }
   string bytes=System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(localPath,"practice.vault")));
   Check(!bytes.Contains("Training medication")&&!bytes.Contains("Fictional instructions"),"Plaintext found on disk.");
   using(var vault=new Vault(localPath)){
    Check(ProviderAccounts.SignIn(vault,"training-admin","PracticeAdmin9?")==null,"Incorrect password accepted.");
    var admin=ProviderAccounts.SignIn(vault,"training-admin","PracticeAdmin9!");
    Check(admin!=null&&admin.Admin,"Persisted administrator could not sign in.");
    var sync=new Sync(vault);
    Check(vault.View(s=>s.Queue.Count)==2,"Queued edits must survive restart.");
    try{sync.Run(dummy,"other");throw new Exception("Practice mismatch accepted.");}catch(IOException){}
    var first=vault.View(s=>s.Queue.First());
    var acknowledged=dummy.Apply(first);
    Check(acknowledged.Revision==dummy.Apply(first).Revision,"Repeated event changed revision.");
    sync.Run(dummy,"demo");
    Check(vault.View(s=>s.Queue.Count)==0,"Replayed acknowledgement or queued update left behind.");
    Check(dummy.Chart(visitId).Records.Count(r=>r.Id=="sample-rx")==1,"Prescription failed to sync exactly once.");
    Check(cloud.View(s=>s.Log.Count(x=>x.Action==eventId))==1,"Duplicate check-in.");
    vault.Save(s=>s.Edit(s.Visits.Single(x=>x.Id==visitId),"provider","record",new Record{Id="note",Kind="Clinical note",Title="Pending",Body="Edit"}));
    cloud.Save(s=>s.Visits.Single(v=>v.Id==visitId).Revision="changed-in-cloud");
    sync.Run(dummy,"demo");
    Check(vault.View(s=>s.Queue.Single().Conflict),"Conflict did not remain queued.");
    Check(!dummy.Chart(visitId).Records.Exists(x=>x.Id=="note"),"Conflict overwrote cloud.");
    vault.Save(s=>{
     var change=s.Queue.Single();change.Conflict=false;change.BaseRevision="changed-in-cloud";
     var current=s.Visits.Single(v=>v.Id==visitId);
     s.Edit(current,"provider","referral",new Record{Id="referral",Kind="Referral",Title="Training referral",Body="Fictional destination"});
     s.Edit(current,"patient:demo","signed-form",new Record{Id="form",Kind="Signed form",Title="Consent",Body="Fictional signature"});
     s.Complete(current,"provider");
    });
    Check(vault.View(s=>s.Queue.Last().Encounter.Records.Count)==4,"Handoff did not contain exactly the current visit entries.");
    Check(dummy.Chart(visitId).CompletedAt==null,"Offline completion reached the adapter prematurely.");
    try{
     vault.Save(s=>s.CheckIn(s.Visits.Single(v=>v.Id==visitId),"provider"));
     throw new Exception("Completed appointment could be checked in again.");
    }catch(InvalidOperationException){}
    sync.Run(dummy,"demo");
    sync.Run(dummy,"demo");
    Check(vault.View(s=>s.Queue.Count)==0,"Completion handoff did not clear after acknowledgement.");
    var receiptEvent=cloud.View(s=>s.CompletedEncounters.Single());
    Check(dummy.Apply(receiptEvent).EventId==receiptEvent.Id&&cloud.View(s=>s.CompletedEncounters.Count)==1,
     "Replaying completion created a duplicate handoff.");
    var handedOff=receiptEvent.Encounter;
    Check(handedOff.Records.Any(r=>r.Id=="referral")&&handedOff.Records.Any(r=>r.Id=="sample-rx")&&
     handedOff.Records.Any(r=>r.Id=="note")&&handedOff.Records.Any(r=>r.Id=="form"),"Chart handoff lost visit items.");
    Check(vault.View(s=>s.Visits.Single(v=>v.Id==visitId).CompletedAt)==handedOff.CompletedAt,"Completed status changed on refresh.");
   }
  }
  Console.WriteLine("PASS: admin sign-in, patient verification, check-in, encrypted offline changes, conflicts, and completed-visit handoff.");
 }
 static void CheckSplitConnections(){
  const string practice="practice-test",routing="tenant-test";
  var links=new ChartingConnections{
   Appointments=new ConnectionKey{Url="https://appointments.invalid",KeyId="schedule-id",Secret="schedule-test"},
   Charts=new ConnectionKey{Url="https://charts.invalid",KeyId="chart-id",Secret="chart-test"},
   Writeback=new ConnectionKey{Url="https://writeback.invalid",KeyId="write-id",Secret="write-test"}
  };
  var calls=new List<string>();
  string writerRoute=routing;
  var client=new Remote(links,(key,path,body)=>{
   string role=key==links.Appointments?"appointments":key==links.Charts?"charts":"writeback";
   calls.Add(role+" "+path.Split('?')[0]);
   if(path=="/identity"){
    Check(body==null,"Verification wrote data.");
    return Common.Json.Serialize(new Identity{PracticeId=practice,Practice="Fictional practice",Protocol=Identity.CurrentProtocol,
     RoutingId=role=="writeback"?writerRoute:routing,Role=role});
   }
   if(path.StartsWith("/appointments?"))return "[]";
   if(path.StartsWith("/visits/"))return "{\"Id\":\"fake-visit\"}";
   Check(body is Change,"Writeback did not include a change event.");
   return "{\"EventId\":\"fake-event\",\"Revision\":\"2\"}";
  });
  Check(client.Verify().PracticeId==practice,"Split practice identity did not verify.");
  links.RoutingId=routing;
  client.Schedule(DateTime.UtcNow);client.Chart("fake-visit");client.Apply(new Change{Id="fake-event"});
  Check(calls.SequenceEqual(new[]{"appointments /identity","charts /identity","writeback /identity",
   "appointments /appointments","charts /visits/fake-visit","writeback /changes"}),"A request used the wrong connection.");
  writerRoute="different-tenant";
  try{client.Verify();throw new Exception("Mismatched writeback routing accepted.");}catch(IOException){}
  links.Writeback.Secret=links.Charts.Secret;
  try{links.Validate();throw new Exception("Same key secret accepted for two roles.");}catch(ArgumentException){}
  try{ConnectionKey.NormalizeUrl("http://appointments.invalid");throw new Exception("HTTP accepted.");}catch(ArgumentException){}
 }
}
