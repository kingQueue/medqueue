using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Web.Script.Serialization;
using System.ComponentModel;
using System.Diagnostics;


using System.Security.AccessControl;
using System.Security.Principal;

namespace MedQueue {
public class DriveVerificationException : IOException {
 public DriveVerificationException(string message) : base(message){}
}
public static class Common {
 public static JavaScriptSerializer Json { get { return new JavaScriptSerializer { MaxJsonLength=67108864 }; } }
 public static string Id(){return Guid.NewGuid().ToString("N");}
 public static DateTime Utc(string stamp){return DateTimeOffset.Parse(stamp,System.Globalization.CultureInfo.InvariantCulture,System.Globalization.DateTimeStyles.AssumeUniversal).UtcDateTime;}
 public static string Now(){return DateTime.UtcNow.ToString("o");}
 public static bool Eligible(string stamp,DateTime now){DateTime t=Utc(stamp);return t>=now && t<now.AddHours(24);}
 public static void CheckDrive(string root){
  var drive=new DriveInfo(root);
  string letter=drive.Name.Substring(0,2).ToUpperInvariant();
  if(!System.Text.RegularExpressions.Regex.IsMatch(letter,@"\A[A-Z]:\z"))
   throw new DriveVerificationException("Choose a local drive with a drive letter.");
  if(!drive.IsReady)
   throw new DriveVerificationException("Open "+letter+" in File Explorer and unlock it with Windows, then try again.");
  if(drive.DriveType!=DriveType.Fixed||drive.DriveFormat!="NTFS")
   throw new DriveVerificationException("Choose a fixed NTFS volume.");

  // Elevate only the read-only status query. The app and vault keep the current Windows identity.
  string probe=@"try {
    $ErrorActionPreference = 'Stop'
    Import-Module ($PSHOME + '\Modules\Microsoft.PowerShell.Management\Microsoft.PowerShell.Management.psd1')
    $volume = Get-WmiObject -Namespace 'root\CIMV2\Security\MicrosoftVolumeEncryption' -Class Win32_EncryptableVolume -Filter ""DriveLetter='"+letter+@"'""
    if ($null -eq $volume) { exit 13 }
    $lock = $volume.GetLockStatus()
    if ($lock.ReturnValue -ne 0) { exit 14 }
    if ($lock.LockStatus -ne 0) { exit 12 }
    $protection = $volume.GetProtectionStatus()
    $conversion = $volume.GetConversionStatus()
    if ($protection.ReturnValue -ne 0 -or $conversion.ReturnValue -ne 0) { exit 14 }
    if ($protection.ProtectionStatus -eq 1 -and $conversion.ConversionStatus -eq 1) { exit 0 }
    exit 13
   } catch { exit 14 }";
  string arguments="-NoProfile -NonInteractive -EncodedCommand "+Convert.ToBase64String(Encoding.Unicode.GetBytes(probe));
  string executable=Path.Combine(Environment.SystemDirectory,@"WindowsPowerShell\v1.0\powershell.exe");
  int result;
  try{
   using(var process=Process.Start(new ProcessStartInfo(executable,arguments){
    UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden,WorkingDirectory=Environment.SystemDirectory
   })){
    if(process==null||!process.WaitForExit(60000))
     throw new DriveVerificationException("The Windows BitLocker status check did not finish. Try again.");
    result=process.ExitCode;
   }
  }catch(Win32Exception ex){
   if(ex.NativeErrorCode==1223)
    throw new DriveVerificationException("The Windows administrator prompt was canceled. Approve it to check BitLocker.");
   throw new DriveVerificationException("Windows could not start the BitLocker status check. Ask your IT administrator to verify access.");
  }
  if(result==0)return;
  if(result==12)throw new DriveVerificationException("Unlock "+letter+" in File Explorer, then try again.");
  if(result==13)throw new DriveVerificationException("Turn on BitLocker for "+letter+" and wait for encryption to finish.");
  throw new DriveVerificationException("Windows could not verify BitLocker on "+letter+". Ask IT to check the volume and BitLocker permissions.");
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
