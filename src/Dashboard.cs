using System;
using System.IO;
using System.Linq;
using System.Drawing;
using System.Security.Cryptography;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace MedQueue {
public static class Program {
 [STAThread]
 public static int Main(string[] args){
  Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
  try{
   string home=AppDomain.CurrentDomain.BaseDirectory;
   Common.CheckDrive(Path.GetPathRoot(home));
   using(var vault=new Vault(home))
   using(var dummyVault=new Vault(Path.Combine(home,"training")))
    Application.Run(new Dashboard(vault,new Dummy(dummyVault)));
   return 0;
  }catch(DriveVerificationException ex){MessageBox.Show(ex.Message,"MedQueue");return 1;}
  catch(Exception){MessageBox.Show("MedQueue could not open its protected workspace. Verify BitLocker, the Windows account and whether MedQueue is already running.","MedQueue");return 1;}
 }
}
public class Dashboard : Form {
 readonly Vault vault;readonly Dummy dummy;readonly Sync sync;
 readonly FlowLayoutPanel menu=new FlowLayoutPanel{Dock=DockStyle.Top,Height=95,AutoScroll=true,Padding=new Padding(10)};
 readonly ListBox visits=new ListBox{Dock=DockStyle.Left,Width=300};
 readonly ListBox records=new ListBox{Dock=DockStyle.Top,Height=165};
 readonly TextBox detail=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical};
 readonly Label status=new Label{Dock=DockStyle.Bottom,Height=35,TextAlign=ContentAlignment.MiddleLeft};
 readonly Timer timer=new Timer{Interval=15000};
 string provider,patientVisit,message="Waiting for connection",connectionStatus;bool admin,busy,painting;
 DateTime expires;
 public Dashboard(Vault vault,Dummy dummy){
  this.vault=vault;this.dummy=dummy;sync=new Sync(vault);
  Text="MedQueue | Downtime workspace";Width=1140;Height=710;MinimumSize=new Size(850,500);
  Font=new Font("Segoe UI",11);BackColor=Color.FromArgb(235,244,248);
  var chart=new Panel{Dock=DockStyle.Fill,Padding=new Padding(12)};chart.Controls.Add(detail);chart.Controls.Add(records);
  Controls.Add(chart);Controls.Add(visits);Controls.Add(status);Controls.Add(menu);
  visits.SelectedIndexChanged+=(s,e)=>{if(!painting)OpenChart();};records.SelectedIndexChanged+=(s,e)=>ShowRecord();
  timer.Tick+=async(s,e)=>{if((provider!=null||patientVisit!=null)&&DateTime.UtcNow>expires)Logout();await RefreshCloud();};
  FormClosing+=(s,e)=>{if(busy){e.Cancel=true;Say("Sync is finishing. Close again when it completes.");}};
  Shown+=async(s,e)=>{Draw();timer.Start();await RefreshCloud();};
 }
 class Entry<T>{public T Value;public string Label;public override string ToString(){return Label;}}
 Visit SelectedVisit{get{var e=visits.SelectedItem as Entry<Visit>;return e==null?null:e.Value;}}
 Record SelectedRecord{get{var e=records.SelectedItem as Entry<Record>;return e==null?null:e.Value;}}
 void Say(string text){message=text;Status();}
 void Status(){
  var s=vault.View(x=>new{Pending=x.Queue.Count,Held=x.Queue.Count(e=>e.Conflict),x.PracticeId,x.LastSync});
  status.Text=provider==null?(patientVisit==null?"  "+message:"  Patient check-in     |     "+message):
    "  "+provider+"     |     "+(s.PracticeId=="demo"?"Training clinic":"Practice")+"     |     "+s.Pending+" queued, "+s.Held+" conflicts     |     "+(connectionStatus??"")+(s.LastSync==null?"":"     |     Last sync "+Common.Utc(s.LastSync).ToLocalTime().ToString("g"))+"     |     "+message;
 }
 void Add(string label,Action command){
  var b=new Button{Text=label,AutoSize=true,Padding=new Padding(8,2,8,2),Height=34,FlatStyle=FlatStyle.Flat,BackColor=Color.White};
  b.Click+=(s,e)=>{try{if(provider!=null||patientVisit!=null){if(DateTime.UtcNow>expires){Logout();return;}Touch();}command();}
   catch(IOException){Say("The workspace could not be saved or opened. Check that the drive is available and writable.");}
   catch(Exception){Say("Action failed. Check the workspace or try again.");}};menu.Controls.Add(b);
 }
 void Touch(){expires=DateTime.UtcNow.AddMinutes(5);}
 void Logout(){
  if(provider!=null||patientVisit!=null)vault.Save(s=>s.Audit(provider??"patient:"+patientVisit,"sign out",patientVisit));
  provider=null;patientVisit=null;admin=false;Say("Signed out");Draw();
 }
 void Draw(){
  foreach(Control c in menu.Controls.Cast<Control>().ToArray())c.Dispose();
  visits.Visible=provider!=null;records.Visible=provider!=null;records.Items.Clear();detail.Clear();
  if(provider==null&&patientVisit==null){
   if(vault.View(s=>s.Providers.Count)==0){Add("Create practice administrator",()=>Account(true));}
   else{Add("Provider sign in",Login);Add("Patient check-in",PatientLogin);}
   detail.Text="MedQueue\r\n\r\nSign in to access the downtime workspace.";
  }else if(patientVisit!=null){
   Add("Review and sign form",PatientForm);Add("Finish and sign out",Logout);
   detail.Text="You’re checked in. You can review and sign a form or finish and return the workstation to staff.";
  }else{
   Add("Sync now",()=>{var unused=RefreshCloud();});
   Add("Issue patient access code",Issue);Add("Add or edit record",()=>RecordEditor(false));
   Add("Add or edit prescription",()=>RecordEditor(true));Add("Create or edit form",FormEditor);
   Add("Close visit",CloseVisit);Add("Review sync conflict",ReviewConflict);
   if(admin){Add("Connect cloud",Connect);Add("Add provider",()=>Account(false));Add("Audit log",AuditLog);
    if(vault.View(s=>s.PracticeId=="demo"))Add("Toggle training outage",Toggle);
   }
   Add("Sign out",Logout);Populate();
  }
  Status();
 }
 static string[] Prompt(IWin32Window owner,string title,string[] labels,string[] values=null,int secret=-1,int multiline=-1,int readOnly=-1,int confirmSecret=-1,Func<string[],string> validate=null){
  using(var dialog=new Form{Text=title,Width=660,Height=220+labels.Length*55+(multiline<0?0:120),StartPosition=FormStartPosition.CenterParent,MinimizeBox=false,MaximizeBox=false,Font=new Font("Segoe UI",10)}){
   var layout=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,AutoScroll=true,Padding=new Padding(18)};
   var boxes=new TextBox[labels.Length];
   for(int i=0;i<labels.Length;i++){
    layout.Controls.Add(new Label{Text=labels[i],AutoSize=true});
    var box=new TextBox{Text=values==null?"":values[i],Dock=DockStyle.Top,UseSystemPasswordChar=i==secret||i==confirmSecret,Multiline=i==multiline,ReadOnly=i==readOnly,MaxLength=16000};
    if(i==multiline){box.Height=135;box.ScrollBars=ScrollBars.Vertical;}
    boxes[i]=box;layout.Controls.Add(box);
   }
   var error=new Label{AutoSize=true,ForeColor=Color.DarkRed};
   if(confirmSecret>=0){
    var show=new CheckBox{Text="Show passwords",AutoSize=true};
    show.CheckedChanged+=(s,e)=>{boxes[secret].UseSystemPasswordChar=!show.Checked;boxes[confirmSecret].UseSystemPasswordChar=!show.Checked;};
    layout.Controls.Add(show);
   }
   if(validate!=null)layout.Controls.Add(error);
   var buttons=new FlowLayoutPanel{Dock=DockStyle.Top,Height=42};
   var ok=new Button{Text="Continue",AutoSize=true};var cancel=new Button{Text="Cancel",DialogResult=DialogResult.Cancel,AutoSize=true};
   ok.Click+=(s,e)=>{
    string problem=validate==null?null:validate(boxes.Select(x=>x.Text).ToArray());
    if(problem==null)dialog.DialogResult=DialogResult.OK;
    else error.Text=problem;
   };
   buttons.Controls.Add(ok);buttons.Controls.Add(cancel);layout.Controls.Add(buttons);dialog.Controls.Add(layout);dialog.AcceptButton=ok;dialog.CancelButton=cancel;
   using(var timeout=new Timer{Interval=180000}){timeout.Tick+=(s,e)=>dialog.Close();timeout.Start();if(dialog.ShowDialog(owner)!=DialogResult.OK)return null;}
   return boxes.Select(x=>x.Text).ToArray();
  }
 }
 void Account(bool first){
  string actor=provider;
  string[] a=Prompt(this,first?"Create practice administrator":"Add provider",
   new[]{"Unique provider username",Common.PasswordRequirements,"Confirm password"},secret:1,confirmSecret:2,validate:entries=>{
    if(!ProviderAccounts.ValidName(entries[0].Trim().ToLowerInvariant()))
     return "Use 3–64 letters, numbers, periods, underscores or hyphens for the username.";
    if(!Common.IsNewPasswordValid(entries[1]))return "The password must meet the requirements shown above.";
    if(!String.Equals(entries[1],entries[2],StringComparison.Ordinal))return "The passwords do not match.";
    return null;
   });
  if(a==null)return;
  if(!first&&(provider!=actor||!admin||DateTime.UtcNow>expires))return;
  string name=a[0].Trim().ToLowerInvariant();
  ProviderAccounts.Create(vault,actor,name,a[1],first);
  Draw();Say("Account created");
 }
 void Login(){
  string[] a=Prompt(this,"Provider sign in",new[]{"Username","Password"},null,1);if(a==null)return;
  var account=ProviderAccounts.SignIn(vault,a[0].Trim().ToLowerInvariant(),a[1]);
  if(account==null){Say("Sign-in failed. Check credentials or wait for the lockout to expire.");return;}
  provider=account.Name;admin=account.Admin;Touch();Draw();Say("Signed in");
 }
 void Populate(){
  var previous=SelectedVisit;var previouslySelected=SelectedRecord;
  string id=previous==null?null:previous.Id;
  var rows=vault.View(s=>s.Visits.OrderBy(v=>v.Start).ToList());
  painting=true;
  visits.Items.Clear();foreach(Visit v in rows)visits.Items.Add(new Entry<Visit>{Value=v,Label=Common.Utc(v.Start).ToLocalTime().ToString("g")+"   "+v.Name+(v.Active?"   ·   In visit":"")});
  for(int i=0;i<visits.Items.Count;i++)if(((Entry<Visit>)visits.Items[i]).Value.Id==id)visits.SelectedIndex=i;
  painting=false;
  var current=SelectedVisit;
  if(previous==null||current==null||Common.Json.Serialize(previous)!=Common.Json.Serialize(current)){
   LoadRecords();
   if(previouslySelected!=null){
    for(int i=0;i<records.Items.Count;i++)if(((Entry<Record>)records.Items[i]).Value.Id==previouslySelected.Id){records.SelectedIndex=i;break;}
   }
  }
 }
 void OpenChart(){
  if(provider==null||DateTime.UtcNow>expires)return;
  var v=SelectedVisit;if(v==null)return;
  Touch();vault.Save(s=>s.Audit(provider,"chart viewed",v.Id));LoadRecords();
 }
 void LoadRecords(){
  var v=SelectedVisit;records.Items.Clear();detail.Clear();if(v==null)return;
  foreach(Record r in v.Records)records.Items.Add(new Entry<Record>{Value=r,Label=r.Kind+"   ·   "+r.Title});
  detail.Text=v.Name+"\r\n"+(v.CheckedIn==null?"Awaiting check-in":"Checked in "+Common.Utc(v.CheckedIn).ToLocalTime().ToString("g"));
 }
 void ShowRecord(){
  var r=SelectedRecord;if(provider==null||r==null||DateTime.UtcNow>expires)return;
  Touch();vault.Save(s=>s.Audit(provider,"record viewed",r.Id));detail.Text=r.Title+"\r\n\r\n"+r.Body;
 }
 static string PassHash(string code){
  using(var sha=SHA256.Create())return Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(code)));
 }
 void Issue(){
  var v=SelectedVisit;if(v==null){Say("Select a visit.");return;}
  string code=Common.Secret(9).Replace('+','-').Replace('/','_');string issuer=provider;
  vault.Save(s=>{var x=s.Visits.Single(y=>y.Id==v.Id);x.PassHash=PassHash(code);x.PassUntil=DateTime.UtcNow.AddMinutes(15).ToString("o");s.Audit(issuer,"check-in code issued",v.Id);});
  MessageBox.Show(this,"Confirm the patient’s identity and give them this one-use code.\r\n\r\n"+code,"Patient access");
  Logout();
 }
 void PatientLogin(){
  string[] response=Prompt(this,"Patient check-in",new[]{"One-use check-in code"},null,0);if(response==null)return;
  string id=null;string hash=PassHash(response[0].Trim());
  vault.Save(s=>{
   var v=s.Visits.Find(x=>Common.Equal(hash,x.PassHash)&&Common.Utc(x.PassUntil)>DateTime.UtcNow);
   if(v==null){s.Audit("patient","check-in failed",null);return;}
   id=v.Id;v.PassHash=null;v.PassUntil=null;v.Active=true;
   if(v.CheckedIn==null)s.Edit(v,"patient:"+id,"check-in",null);s.Audit("patient:"+id,"sign in",id);
  });
  if(id==null){Say("Code invalid or expired. Ask staff for a new code.");return;}
  patientVisit=id;Touch();Draw();Say("Checked in");
 }
 void PatientForm(){
  string visitId=patientVisit;
  var forms=vault.View(s=>s.Forms.ToList());var selected=Choose("Choose a form",forms.Select(f=>f.Title).ToArray());if(selected<0)return;
  var def=forms[selected];string[] a=Prompt(this,"Review and sign",new[]{def.Title,"Type your full name to sign this form"},new[]{def.Body,""},-1,0,0);
  if(a==null||String.IsNullOrWhiteSpace(a[1])||patientVisit!=visitId||DateTime.UtcNow>expires)return;
  vault.Save(s=>{
   var v=s.Visits.Single(x=>x.Id==visitId);
   if(!String.Equals(v.Name.Trim(),a[1].Trim(),StringComparison.OrdinalIgnoreCase))throw new ArgumentException("Signature name differs.");
   if(v.Records.Exists(x=>x.Id=="form-"+def.Id+"-"+def.Version))throw new InvalidOperationException();
   s.Edit(v,"patient:"+v.Id,"signed-form",new Record{Id="form-"+def.Id+"-"+def.Version,Kind="Signed form",Title=def.Title,Body=def.Body+"\r\n\r\nSigned by "+a[1].Trim()+" at "+Common.Now()+" (version "+def.Version+")"});
  });Say("Form signed");
 }
 int Choose(string title,string[] options){
  using(var dialog=new Form{Text=title,Width=480,Height=330,StartPosition=FormStartPosition.CenterParent,Font=Font}){
   var list=new ListBox{Dock=DockStyle.Fill};list.Items.AddRange(options);var ok=new Button{Text="Select",Dock=DockStyle.Bottom,DialogResult=DialogResult.OK};
   dialog.Controls.Add(list);dialog.Controls.Add(ok);dialog.AcceptButton=ok;
   using(var timeout=new Timer{Interval=120000}){timeout.Tick+=(s,e)=>dialog.Close();timeout.Start();return dialog.ShowDialog(this)==DialogResult.OK?list.SelectedIndex:-1;}
  }
 }
 void FormEditor(){
  string actor=provider;
  var forms=vault.View(s=>s.Forms.ToArray());int i=Choose("Form templates",new[]{"Create a form"}.Concat(forms.Select(template=>template.Title)).ToArray());if(i<0)return;
  var f=i==0?new FormDef{Id=Common.Id(),Title="",Body=""}:forms[i-1];
  var a=Prompt(this,"Form template",new[]{"Title","Form text"},new[]{f.Title,f.Body},-1,1);
  if(a==null||a.Any(String.IsNullOrWhiteSpace)||provider!=actor||DateTime.UtcNow>expires)return;
  f.Title=a[0].Trim();f.Body=a[1];f.Version++;
  vault.Save(s=>{s.Forms.RemoveAll(x=>x.Id==f.Id);s.Forms.Add(f);s.Audit(actor,"form template saved",f.Id);});Say("Form template saved");
 }
 void RecordEditor(bool medication){
  var v=SelectedVisit;if(v==null){Say("Select a visit.");return;}
  var r=SelectedRecord;string actor=provider;
  bool edit=r!=null&&r.Kind!="Signed form"&&(r.Kind=="Prescription")==medication&&
   MessageBox.Show(this,"Edit the selected item? Choose No to add an item.","Record",MessageBoxButtons.YesNo)==DialogResult.Yes;
  var a=Prompt(this,medication?"Prescription documentation":"Patient record",
   new[]{medication?"Medication":"Title",medication?"Dose, route, frequency and instructions":"Record text"},
   edit?new[]{r.Title,r.Body}:null,-1,1);
  if(a==null||a.Any(String.IsNullOrWhiteSpace)||provider!=actor||DateTime.UtcNow>expires)return;
  vault.Save(s=>{
   var current=s.Visits.Single(x=>x.Id==v.Id);
   if(edit){
    var latest=current.Records.Find(x=>x.Id==r.Id);
    if(latest==null||latest.Body!=r.Body||latest.Title!=r.Title||latest.Kind!=r.Kind)throw new InvalidOperationException();
   }
   s.Edit(current,actor,medication?"prescription":"record",new Record{Id=edit?r.Id:Common.Id(),Kind=medication?"Prescription":edit?r.Kind:"Clinical note",Title=a[0].Trim(),Body=a[1]});
  });
  Populate();LoadRecords();Say("Saved to the encrypted queue");
 }
 void CloseVisit(){
  var v=SelectedVisit;if(v==null)return;
  vault.Save(s=>{var x=s.Visits.Single(y=>y.Id==v.Id);x.Active=false;x.PassHash=null;x.PassUntil=null;s.Audit(provider,"visit closed",v.Id);});
  Populate();Say("Visit closed");
 }
 void AuditLog(){
  vault.Save(s=>s.Audit(provider,"audit viewed",null));
  var lines=vault.View(s=>s.Log.Skip(Math.Max(0,s.Log.Count-200)).Select(x=>x.At+"   "+x.Actor+"   "+x.Action+"   "+x.Target).ToArray());
  detail.Text=String.Join("\r\n",lines);
 }
 void Toggle(){
  vault.Save(s=>{s.Offline=!s.Offline;s.Audit(provider,s.Offline?"training outage started":"training outage ended",null);});
  var unused=RefreshCloud();
 }
 async void ReviewConflict(){
  if(busy){Say("Wait for the current sync.");return;}
  var v=SelectedVisit;if(v==null){Say("Select a visit.");return;}
  var e=vault.View(s=>s.Queue.FirstOrDefault(x=>x.VisitId==v.Id&&x.Conflict));
  if(e==null){Say("This visit has no sync conflict.");return;}
  string actor=provider;busy=true;
  try{
   var setup=vault.View(s=>new{s.Endpoint,s.Token,s.PracticeId,s.Offline});
   if(setup.Offline){Say("Reconnect before reviewing a conflict.");return;}
   ICloud target=setup.PracticeId=="demo"?(ICloud)dummy:new Remote(setup.Endpoint,setup.Token);
   var remote=await Task.Run(()=>{
    if(target.Verify().PracticeId!=setup.PracticeId)throw new InvalidOperationException();
    return target.Chart(v.Id);
   });
   if(provider!=actor||DateTime.UtcNow>expires)return;
   if(remote.Id!=v.Id||remote.PatientId!=v.PatientId||String.IsNullOrWhiteSpace(remote.Revision))throw new InvalidOperationException();
   vault.Save(s=>s.Audit(actor,"conflict chart reviewed",v.Id));
   var current=e.Record==null?null:remote.Records.Find(r=>r.Id==e.Record.Id);
   string cloud=e.Record==null?remote.CheckedIn??"(awaiting check-in)":current==null?"(no matching record)":current.Title+"\r\n"+current.Body;
   string local=e.Record==null?e.At:e.Record.Title+"\r\n"+e.Record.Body;
   var a=Prompt(this,"Review conflict",new[]{"Cloud value, then pending local value","Type APPLY LOCAL to send this change against the reviewed revision"},
    new[]{"CLOUD\r\n"+cloud+"\r\n\r\nLOCAL\r\n"+local,""},-1,0,0);
   if(a==null||a[1]!="APPLY LOCAL"||provider!=actor||DateTime.UtcNow>expires)return;
   vault.Save(s=>{
    var head=s.Queue.First(x=>x.VisitId==v.Id);if(head.Id!=e.Id||!head.Conflict)throw new InvalidOperationException();
    head.BaseRevision=remote.Revision;head.Conflict=false;s.Audit(actor,"authorized conflict retry",head.Id);
   });
   Say("Reviewed change queued for retry");
  }catch(Exception){Say("Conflict review unavailable. The pending change is still saved.");}
  finally{busy=false;}
  await RefreshCloud();
 }
 async void Connect(){
  if(busy){Say("Wait for the current sync.");return;}
  var linked=vault.View(s=>new{s.PracticeId,s.Endpoint});
  if(linked.PracticeId=="demo"){Say("This is a training workspace. Install a separate practice workspace to link an adapter.");return;}
  int mode=linked.PracticeId==null?Choose("Cloud connection",new[]{"Link an HTTPS adapter","Start fictional training clinic"}):0;if(mode<0)return;
  string actor=provider;busy=true;
  try{
   if(mode==1){
    if(MessageBox.Show(this,"Use only fictional information in this training workspace.","Start training",MessageBoxButtons.OKCancel)!=DialogResult.OK)return;
    dummy.Seed();vault.Save(s=>{s.PracticeId="demo";s.Audit(actor,"training linked","demo");});
   }else{
    var a=Prompt(this,"Verify cloud adapter",new[]{"HTTPS adapter base URL","Bearer credential"},new[]{linked.Endpoint??"",""},1);
    if(a==null)return;
    var remote=new Remote(a[0].Trim(),a[1].Trim());
    var identity=await Task.Run(()=>{var i=remote.Verify();if(remote.Schedule(DateTime.UtcNow)==null)throw new IOException();return i;});
    if(actor!=provider||DateTime.UtcNow>expires)return;
    if(identity.PracticeId=="demo")throw new InvalidOperationException();
    if(linked.PracticeId!=null&&linked.PracticeId!=identity.PracticeId){Say("This workspace is bound to another practice.");return;}
    if(MessageBox.Show(this,"Verified "+identity.Practice+" ("+identity.PracticeId+"). Link this adapter?","Cloud identity",MessageBoxButtons.YesNo)!=DialogResult.Yes)return;
    if(actor!=provider||DateTime.UtcNow>expires)return;
    vault.Save(s=>{s.Endpoint=a[0].Trim();s.Token=a[1].Trim();s.PracticeId=identity.PracticeId;s.Audit(actor,"adapter verified",identity.PracticeId);});
   }
   Draw();Say("Cloud connection linked");
  }catch(Exception){Say("Cloud verification failed. Check the HTTPS address, credential and adapter protocol.");}
  finally{busy=false;}
  await RefreshCloud();
 }
 async Task RefreshCloud(){
  if(busy)return;
  var cutoff=DateTime.UtcNow.AddHours(-24);
  if(vault.View(s=>s.Visits.Exists(v=>!v.Active&&Common.Utc(v.Start)<cutoff&&!s.Queue.Exists(e=>e.VisitId==v.Id))))
   vault.Save(s=>s.Visits.RemoveAll(v=>!v.Active&&Common.Utc(v.Start)<cutoff&&!s.Queue.Exists(e=>e.VisitId==v.Id)));
  var setup=vault.View(s=>new{s.Endpoint,s.Token,s.PracticeId,s.Offline});
  if(setup.PracticeId==null){connectionStatus="Cloud setup required";Status();return;}
  if(setup.Offline){connectionStatus="Simulated outage";Status();return;}
  busy=true;
  try{
   ICloud target=setup.PracticeId=="demo"?(ICloud)dummy:new Remote(setup.Endpoint,setup.Token);
   await Task.Run(()=>sync.Run(target,setup.PracticeId));connectionStatus="Connected";
   if(provider!=null&&Form.ActiveForm==this)Populate();
  }catch(Exception){connectionStatus="Sync unavailable · retrying automatically";}
  finally{busy=false;Status();}
 }
}
}
