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
using System.Runtime.InteropServices;
using System.Globalization;

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
 const string PasswordSymbols="!@#_+-=";
 public const string PasswordRequirements="Password (14–256 characters, with A–Z, 0–9 and one of "+PasswordSymbols+")";
 public static bool IsNewPasswordValid(string password){
  return password!=null&&password.Length>=14&&password.Length<=256
   &&password.Any(c=>c>='A'&&c<='Z')
   &&password.Any(c=>c>='0'&&c<='9')
   &&password.Any(c=>PasswordSymbols.IndexOf(c)>=0)
   &&password.All(c=>char.IsLetterOrDigit(c)||PasswordSymbols.IndexOf(c)>=0);
 }
 public static string Hash(string password,string salt){using(var k=new Rfc2898DeriveBytes(password,Convert.FromBase64String(salt),600000,HashAlgorithmName.SHA256))return Convert.ToBase64String(k.GetBytes(32));}
 public static bool Equal(string a,string b){if(a==null||b==null||a.Length!=b.Length)return false;int diff=0;for(int i=0;i<a.Length;i++)diff|=a[i]^b[i];return diff==0;}
}
public class Provider {public string Name,Salt,Hash,LockedUntil;public int Failures;public bool Admin;}
public static class ProviderAccounts {
 public static bool ValidName(string name){
  return System.Text.RegularExpressions.Regex.IsMatch(name,@"\A[a-z0-9_.-]{3,64}\z");
 }
 public static void Create(Vault vault,string actor,string name,string password,bool first){
  if(!ValidName(name)||!Common.IsNewPasswordValid(password))throw new ArgumentException("Invalid account credentials.");
  var account=new Provider{Name=name,Salt=Common.Secret(16),Admin=first};
  account.Hash=Common.Hash(password,account.Salt);
  vault.Save(s=>{
   if(first?s.Providers.Count!=0:!s.Providers.Any(p=>p.Name==actor&&p.Admin))throw new InvalidOperationException("Account creation is unavailable.");
   if(s.Providers.Any(p=>p.Name==name))throw new InvalidOperationException("The provider username is already in use.");
   s.Providers.Add(account);
   s.Audit(actor??name,"provider created",name);
  });
 }
 public static Provider SignIn(Vault vault,string name,string password){
  Provider authenticated=null;
  vault.Save(s=>{
   var provider=s.Providers.Find(p=>p.Name==name);
   string guess=Common.Hash(password,provider==null?Convert.ToBase64String(new byte[16]):provider.Salt);
   if(provider!=null&&(provider.LockedUntil==null||Common.Utc(provider.LockedUntil)<=DateTime.UtcNow)){
    if(Common.Equal(guess,provider.Hash)){
     provider.Failures=0;provider.LockedUntil=null;authenticated=provider;
    }else if(++provider.Failures>=5){
     provider.Failures=0;provider.LockedUntil=DateTime.UtcNow.AddMinutes(10).ToString("o");
    }
   }
   s.Audit(provider==null?"unknown":name,authenticated==null?"sign-in failed":"sign in",null);
  });
  return authenticated;
 }
}
public class FormDef {public string Id,Title,Body;public int Version;}
public class Record {public string Id,Kind,Title,Body,EncounterId;}
public class Visit {
 public string Id,PatientId,Name,BirthDate,Phone,Start,Revision,CheckedIn,CompletedAt,CompletedBy,PassHash,PassUntil;
 public bool Active;
 public List<Record> Records=new List<Record>();
}
public class EncounterHandoff {
 public string ScheduledAt,CheckedInAt,CompletedAt,CompletedBy;
 public List<Record> Records;
}
public class Change {
 public string Id,VisitId,PatientId,Actor,At,Kind,BaseRevision;
 public Record Record;
 public EncounterHandoff Encounter;
 public bool Conflict;
}
public static class PatientIdentity {
 public static bool ValidBirthDate(string date){
  DateTime value;
  return DateTime.TryParseExact(date,"yyyy-MM-dd",CultureInfo.InvariantCulture,DateTimeStyles.None,out value)&&value.Date<=DateTime.Today;
 }
 public static string PhoneDigits(string phone){
  if(String.IsNullOrWhiteSpace(phone))return null;
  if(!phone.All(c=>(c>='0'&&c<='9')||" +-().".IndexOf(c)>=0))return null;
  string digits=new string(phone.Where(c=>c>='0'&&c<='9').ToArray());
  return digits.Length>=7&&digits.Length<=15?digits:null;
 }
 static string Name(string value){return String.Join(" ",(value??"").Split((char[])null,StringSplitOptions.RemoveEmptyEntries));}
 public static List<Visit> Find(State state,string actor,string name,string birthDate,string phone){
  string digits=PhoneDigits(phone);
  string normalizedName=Name(name);
  if(!ValidBirthDate(birthDate)||digits==null||normalizedName.Length==0)return new List<Visit>();
  var matches=state.Visits.Where(v=>String.Equals(Name(v.Name),normalizedName,StringComparison.OrdinalIgnoreCase)
    &&v.BirthDate==birthDate&&PhoneDigits(v.Phone)==digits).OrderBy(v=>Common.Utc(v.Start)).ToList();
  if(matches.Select(v=>v.PatientId).Distinct().Skip(1).Any())matches.Clear();
  state.Audit(actor,matches.Count==0?"patient verification failed":"patient verified",matches.Count==0?null:matches[0].PatientId);
  return matches;
 }
}
public class Audit {public string At,Actor,Action,Target;}
public class ConnectionKey {
 public string Url,KeyId,Secret;
 public static string NormalizeUrl(string value){
  Uri uri;
  if(!Uri.TryCreate(value,UriKind.Absolute,out uri)||uri.Scheme!="https"||uri.UserInfo!=""||uri.Query!=""||uri.Fragment!="")
   throw new ArgumentException("Use an HTTPS base URL without credentials, a query, or a fragment.");
  return uri.AbsoluteUri.TrimEnd('/');
 }
 public void Validate(){
  NormalizeUrl(Url);
  if(!IsKey(KeyId)||!IsKey(Secret))throw new ArgumentException("Enter an API key ID and secret without spaces or control characters.");
 }
 internal static bool IsKey(string value){
  return !String.IsNullOrEmpty(value)&&value.Length<=4096&&value.All(c=>c>='!'&&c<='~');
 }
}
public class ChartingConnections {
 public ConnectionKey Appointments,Charts,Writeback;
 public string RoutingId;
 public void Validate(){
  if(Appointments==null||Charts==null||Writeback==null)throw new ArgumentException("Configure all three connections.");
  Appointments.Validate();Charts.Validate();Writeback.Validate();
  if(new[]{Appointments.Secret,Charts.Secret,Writeback.Secret}.Distinct(StringComparer.Ordinal).Count()!=3)
   throw new ArgumentException("Use a different API secret for each connection.");
 }
}
public class State {
 public int Format=1;public string Endpoint,Token,LastSync,PracticeId;public bool Offline;
 public ChartingConnections Connections;
 public List<Provider> Providers=new List<Provider>();public List<FormDef> Forms=new List<FormDef>();public List<Visit> Visits=new List<Visit>();public List<Change> Queue=new List<Change>();public List<Audit> Log=new List<Audit>();
 public List<Change> CompletedEncounters=new List<Change>();
 public void Audit(string actor,string action,string target){Log.Add(new Audit{At=Common.Now(),Actor=actor,Action=action,Target=target});}
 public void CheckIn(Visit visit,string actor){
  if(visit.CompletedAt!=null)throw new InvalidOperationException("This visit is already completed.");
  if(visit.CheckedIn==null){
   visit.CheckedIn=Common.Now();
   Enqueue(visit,actor,"check-in",visit.CheckedIn,null,null);
  }
  visit.Active=true;
 }
 public void Edit(Visit visit,string actor,string kind,Record record){
  if(visit.CheckedIn==null||visit.CompletedAt!=null)throw new InvalidOperationException("This visit is not open for editing.");
  record.EncounterId=visit.Id;
  visit.Records.RemoveAll(x=>x.Id==record.Id);
  visit.Records.Add(record);
  Enqueue(visit,actor,kind,Common.Now(),record,null);
 }
 public void Complete(Visit visit,string actor){
  if(visit.CheckedIn==null||visit.CompletedAt!=null)throw new InvalidOperationException("This visit cannot be completed.");
  visit.CompletedAt=Common.Now();
  visit.CompletedBy=actor;
  visit.Active=false;visit.PassHash=null;visit.PassUntil=null;
  var summary=new EncounterHandoff{ScheduledAt=visit.Start,CheckedInAt=visit.CheckedIn,
   CompletedAt=visit.CompletedAt,CompletedBy=actor,Records=visit.Records.Where(x=>x.EncounterId==visit.Id).ToList()};
  Enqueue(visit,actor,"visit-completed",visit.CompletedAt,null,summary);
 }
 void Enqueue(Visit visit,string actor,string kind,string at,Record record,EncounterHandoff summary){
  Queue.Add(new Change{Id=Common.Id(),VisitId=visit.Id,PatientId=visit.PatientId,Actor=actor,At=at,
   BaseRevision=visit.Revision,Kind=kind,Record=record,Encounter=summary});
  Audit(actor,kind,visit.Id);
 }
}
public class Vault : IDisposable {
 const int ReplaceExisting=1;
 const int WriteThrough=8;
 [DllImport("kernel32.dll",EntryPoint="MoveFileExW",CharSet=CharSet.Unicode,ExactSpelling=true,SetLastError=true)]
 [return: MarshalAs(UnmanagedType.Bool)]
 static extern bool MoveFileEx(string source,string destination,int flags);

 readonly string file;readonly FileStream lease;readonly object gate=new object();
 public Vault(string folder){file=Path.Combine(folder,"practice.vault");lease=new FileStream(Path.Combine(folder,"workspace.lock"),FileMode.OpenOrCreate,FileAccess.ReadWrite,FileShare.None);if(!File.Exists(file)){if(File.Exists(file+".new")||File.Exists(file+".initialized"))throw new IOException("Vault missing. Recover the practice backup.");Write(new State());File.WriteAllText(file+".initialized","1");}}
 State Read(){var bytes=ProtectedData.Unprotect(File.ReadAllBytes(file),null,DataProtectionScope.CurrentUser);try{var s=Common.Json.Deserialize<State>(Encoding.UTF8.GetString(bytes));if(s==null||s.Format!=1)throw new IOException("Unsupported vault.");return s;}finally{Array.Clear(bytes,0,bytes.Length);}}
 void Write(State s){var plain=Encoding.UTF8.GetBytes(Common.Json.Serialize(s));byte[] enc;try{enc=ProtectedData.Protect(plain,null,DataProtectionScope.CurrentUser);}finally{Array.Clear(plain,0,plain.Length);}
  string staged=file+".new";
  using(var f=new FileStream(staged,FileMode.Create,FileAccess.Write,FileShare.None)){f.Write(enc,0,enc.Length);f.Flush(true);}
  // Replace with a same-directory rename so the new file retains the workspace's private ACL.
  if(!MoveFileEx(staged,file,ReplaceExisting|WriteThrough))
   throw new IOException("Could not commit the encrypted vault.",new Win32Exception(Marshal.GetLastWin32Error()));
 }
 public T View<T>(Func<State,T> op){lock(gate)return op(Read());}
 public void Save(Action<State> op){lock(gate){var s=Read();op(s);Write(s);}}
 public void Dispose(){lock(gate)lease.Dispose();}
}
}
