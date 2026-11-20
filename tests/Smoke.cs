using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using MedQueue;

class Smoke {
 static void Check(bool condition,string message){if(!condition)throw new Exception(message);}
 static void Main(){
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
    visitId=vault.View(s=>s.Visits.First().Id);
    vault.Save(s=>{
     var visit=s.Visits.Single(x=>x.Id==visitId);
     s.Edit(visit,"patient:demo","check-in",null);
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
   }
  }
  Console.WriteLine("PASS: 24-hour filter, encrypted vault, restart durability, adapter identity, idempotent replay, prescription sync, conflict preservation.");
 }
}
