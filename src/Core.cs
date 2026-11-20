using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.Management;


using System.Security.AccessControl;
using System.Security.Principal;

namespace MedQueue {
public static class Common {
 public static JavaScriptSerializer Json { get { return new JavaScriptSerializer { MaxJsonLength=67108864 }; } }
 public static string Id(){return Guid.NewGuid().ToString("N");}
 public static DateTime Utc(string stamp){return DateTimeOffset.Parse(stamp,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal).UtcDateTime;}
 public static string Now(){return DateTime.UtcNow.ToString("o");}
 public static bool Eligible(string stamp,DateTime now){DateTime t=Utc(stamp);return t>=now && t<now.AddHours(24);}
 public static void CheckDrive(string root){
  DriveInfo d=new DriveInfo(root);
  if(d.DriveType!=DriveType.Fixed || d.DriveFormat!="NTFS") throw new IOException("Select a fixed NTFS drive.");
  using(var q=new ManagementObjectSearcher(@"root\CIMV2\Security\MicrosoftVolumeEncryption","SELECT * FROM Win32_EncryptableVolume WHERE DriveLetter='"+d.Name.Substring(0,2)+"'")){
   foreach(ManagementObject v in q.Get()){
    using(var p=v.InvokeMethod("GetProtectionStatus",null,null))
    using(var c=v.InvokeMethod("GetConversionStatus",null,null)){
     if(Convert.ToUInt32(p["ReturnValue"])==0 && Convert.ToUInt32(c["ReturnValue"])==0 && Convert.ToInt32(p["ProtectionStatus"])==1 && Convert.ToInt32(c["ConversionStatus"])==1)return;
    }
   }
  }
  throw new IOException("Enable BitLocker and finish encryption before continuing.");
 }
 public static void PrivateFolder(string folder){
  if(Directory.Exists(folder)||File.Exists(folder))throw new IOException("The destination already exists.");
  var acl=new DirectorySecurity();acl.SetAccessRuleProtection(true,false);
  foreach(var sid in new[]{WindowsIdentity.GetCurrent().User,new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null)})
   acl.AddAccessRule(new FileSystemAccessRule(sid,FileSystemRights.FullControl,InheritanceFlags.ObjectInherit|InheritanceFlags.ContainerInherit,PropagationFlags.None,AccessControlType.Allow));
  Directory.CreateDirectory(folder,acl);
 }
 public static string Secret(int bytes){var b=new byte[bytes];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(b);return Convert.ToBase64String(b);}
 public static string Hash(string password,string salt){using(var k=new Rfc2898DeriveBytes(password,Convert.FromBase64String(salt),600000,HashAlgorithmName.SHA256))return Convert.ToBase64String(k.GetBytes(32));}
 public static bool Equal(string a,string b){if(a==null||b==null||a.Length!=b.Length)return false;int diff=0;for(int i=0;i<a.Length;i++)diff|=a[i]^b[i];return diff==0;}
}
public class Provider {public string Name,Salt,Hash,LockedUntil;public int Failures;public bool Admin;}
public class FormDef {public string Id,Title,Body;public int Version;}
public class Record {public string Id,Kind,Title,Body;}
public class Visit {public string Id,PatientId,Name,Start,Revision,CheckedIn,PassHash,PassUntil;public bool Active;public List<Record> Records=new List<Record>();}
public class Change {public string Id,VisitId,PatientId,Actor,At,Kind,BaseRevision;public Record Record;public bool Conflict;}
public class Audit {public string At,Actor,Action,Target;}
public class State {
 public int Format=1;public string Endpoint,Token,LastSync,PracticeId;public bool Offline;
 public List<Provider> Providers=new List<Provider>();public List<FormDef> Forms=new List<FormDef>();public List<Visit> Visits=new List<Visit>();public List<Change> Queue=new List<Change>();public List<Audit> Log=new List<Audit>();
 public void Audit(string actor,string action,string target){Log.Add(new Audit{At=Common.Now(),Actor=actor,Action=action,Target=target});}
 public void Edit(Visit v,string actor,string kind,Record r){
  if(kind=="check-in")v.CheckedIn=Common.Now();else {v.Records.RemoveAll(x=>x.Id==r.Id);v.Records.Add(r);}
  Queue.Add(new Change{Id=Common.Id(),VisitId=v.Id,PatientId=v.PatientId,Actor=actor,At=Common.Now(),BaseRevision=v.Revision,Kind=kind,Record=r});Audit(actor,kind,v.Id);
 }
}
public class Vault : IDisposable {
 readonly string file;readonly FileStream lease;readonly object gate=new object();
 public Vault(string folder){file=Path.Combine(folder,"practice.vault");lease=new FileStream(Path.Combine(folder,"workspace.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);if(!File.Exists(file)){if(File.Exists(file+".new")||File.Exists(file+".initialized"))throw new IOException("Vault missing. Recover the practice backup.");Write(new State());File.WriteAllText(file+".initialized","1");}}
 State Read(){var bytes=ProtectedData.Unprotect(File.ReadAllBytes(file),null,DataProtectionScope.CurrentUser);try{var s=Common.Json.Deserialize<State>(Encoding.UTF8.GetString(bytes));if(s==null||s.Format!=1)throw new IOException("Unsupported vault.");return s;}finally{Array.Clear(bytes,0,bytes.Length);}}
 void Write(State s){var plain=Encoding.UTF8.GetBytes(Common.Json.Serialize(s));byte[] enc;try{enc=ProtectedData.Protect(plain,null,DataProtectionScope.CurrentUser);}finally{Array.Clear(plain,0,plain.Length);}
 using(var f=new FileStream(file+".new",FileMode.Create,FileAccess.Write,FileShare.None)){f.Write(enc,0,enc.Length);f.Flush(true);}if(File.Exists(file))File.Replace(file+".new",file,null);else File.Move(file+".new",file);}
 public T View<T>(Func<State,T> op){lock(gate)return op(Read());}
 public void Save(Action<State> op){lock(gate){var s=Read();op(s);Write(s);}}
 public void Dispose(){lock(gate)lease.Dispose();}
}
}
