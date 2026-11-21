using System;
using System.Collections.Generic;
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
 readonly TextBox visitDetail=new TextBox{Dock=DockStyle.Fill,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical};
 readonly Label visitHeading=new Label{Dock=DockStyle.Top,Height=108,Padding=new Padding(4),Visible=false};
 readonly TabControl sections=new TabControl{Dock=DockStyle.Fill,Visible=false};
 readonly Panel sectionContent=new Panel{Dock=DockStyle.Fill,Padding=new Padding(6)};
 readonly Button editItem=new Button{Dock=DockStyle.Top,Height=36};
 readonly Dictionary<string,Visit> verifiedVisits=new Dictionary<string,Visit>();
 static readonly string[] changeKinds={"record","prescription","referral"};
 readonly Label status=new Label{Dock=DockStyle.Bottom,Height=35,TextAlign=ContentAlignment.MiddleLeft};
 readonly Timer timer=new Timer{Interval=15000};
 string provider,patientVisit,message="Waiting for connection",connectionStatus;bool admin,busy,painting,demoSeeded;
 DateTime expires;
 public Dashboard(Vault vault,Dummy dummy){
  this.vault=vault;this.dummy=dummy;sync=new Sync(vault);
  Text="MedQueue | Downtime workspace";Width=1140;Height=710;MinimumSize=new Size(850,500);
  Font=new Font("Segoe UI",11);BackColor=Color.FromArgb(235,244,248);
  var chart=new Panel{Dock=DockStyle.Fill,Padding=new Padding(12)};
  sectionContent.Controls.Add(visitDetail);sectionContent.Controls.Add(records);sectionContent.Controls.Add(editItem);
  foreach(string section in new[]{"Doctor chart","Prescriptions","Referrals"})sections.TabPages.Add(section);
  sections.TabPages[0].Controls.Add(sectionContent);
  sections.SelectedIndexChanged+=(s,e)=>{sections.SelectedTab.Controls.Add(sectionContent);LoadSection();};
  editItem.Click+=(s,e)=>RunAction(()=>RecordEditor(changeKinds[sections.SelectedIndex]));
  chart.Controls.Add(detail);chart.Controls.Add(sections);chart.Controls.Add(visitHeading);
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
  b.Click+=(s,e)=>RunAction(command);menu.Controls.Add(b);
 }
 void RunAction(Action command){
  try{if(provider!=null||patientVisit!=null){if(DateTime.UtcNow>expires){Logout();return;}Touch();}command();}
   catch(IOException){Say("The workspace could not be saved or opened. Check that the drive is available and writable.");}
   catch(Exception){Say("Action failed. Check the workspace or try again.");}
 }
 void Touch(){expires=DateTime.UtcNow.AddMinutes(5);}
 void Logout(){
  if(provider!=null||patientVisit!=null)vault.Save(s=>s.Audit(provider??"patient:"+patientVisit,"sign out",patientVisit));
  provider=null;patientVisit=null;admin=false;verifiedVisits.Clear();visits.Items.Clear();Say("Signed out");Draw();
 }
 void Draw(){
  foreach(Control c in menu.Controls.Cast<Control>().ToArray())c.Dispose();
  visits.Visible=provider!=null;visitHeading.Visible=false;sections.Visible=false;detail.Visible=true;
  records.Items.Clear();detail.Clear();visitDetail.Clear();
  if(provider==null&&patientVisit==null){
   if(vault.View(s=>s.Providers.Count)==0){Add("Create practice administrator",()=>Account(true));}
   else{Add("Provider sign in",Login);Add("Patient check-in",PatientLogin);}
   detail.Text="MedQueue\r\n\r\nSign in to access the downtime workspace.";
  }else if(patientVisit!=null){
   Add("Review and sign form",PatientForm);Add("Finish and sign out",Logout);
   detail.Text="You’re checked in. You can review and sign a form or finish and return the workstation to staff.";
  }else{
   Add("Find and verify patient",FindPatient);Add("Mark patient signed in",CheckInPatient);
   Add("Issue patient access code",Issue);Add("Complete appointment",CompleteVisit);
   Add("Create or edit form",FormEditor);Add("Review sync conflict",ReviewConflict);
   Add("Sync now",()=>{var unused=RefreshCloud();});
   if(admin){Add("Charting connections",Connect);Add("Add provider",()=>Account(false));Add("Audit log",AuditLog);
    var practice=vault.View(s=>s.PracticeId);
    if(practice=="demo")Add("Toggle training outage",Toggle);
    if(practice==null)Add("Start training clinic",StartTraining);
   }
   Add("Sign out",Logout);Populate();LoadRecords();
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
  ShowProviderDashboard(account);
 }
 internal void ShowProviderDashboard(Provider account){
  provider=account.Name;admin=account.Admin;Touch();Draw();Say("Signed in");
 }
 void FindPatient(){
  string actor=provider;
  visits.ClearSelected();LoadRecords();
  var input=Prompt(this,"Verify patient",new[]{"Full name","Date of birth (YYYY-MM-DD)","Contact phone (include country code if listed)"},
   validate:values=>String.IsNullOrWhiteSpace(values[0])||!PatientIdentity.ValidBirthDate(values[1].Trim())||PatientIdentity.PhoneDigits(values[2])==null
    ?"Enter a name, a valid date of birth, and a phone number.":null);
  if(input==null||actor!=provider||DateTime.UtcNow>expires)return;
  List<Visit> candidates=null;
  vault.Save(s=>candidates=PatientIdentity.Find(s,actor,input[0],input[1].Trim(),input[2]));
  if(candidates.Count==0){Say("No unique patient matched all three details in the local vault.");return;}
  int choice=candidates.Count==1?0:Choose("Select the verified appointment",candidates.Select(VisitLabel).ToArray());
  if(choice<0||actor!=provider||DateTime.UtcNow>expires)return;
  var selected=candidates[choice];
  verifiedVisits[selected.Id]=selected;
  Populate();
  for(int i=0;i<visits.Items.Count;i++)if(((Entry<Visit>)visits.Items[i]).Value.Id==selected.Id){visits.SelectedIndex=i;break;}
  LoadRecords();Say(SelectedVisit!=null&&SelectedVisit.Id==selected.Id?
   "Patient identity confirmed.":"Patient details changed during verification. Try again.");
 }
 static string VisitLabel(Visit v){
  return Common.Utc(v.Start).ToLocalTime().ToString("g")+"   "+v.Name+
   (v.CompletedAt!=null?"   ·   Completed":v.CheckedIn!=null?"   ·   In visit":"");
 }
 bool IsVerified(Visit visit){
  Visit verified;
  return verifiedVisits.TryGetValue(visit.Id,out verified)&&verified.PatientId==visit.PatientId&&
   verified.Name==visit.Name&&verified.BirthDate==visit.BirthDate&&verified.Phone==visit.Phone;
 }
 void Populate(){
  var previous=SelectedVisit;var previouslySelected=SelectedRecord;
  string id=previous==null?null:previous.Id;
  var rows=vault.View(s=>s.Visits.Where(IsVerified).OrderBy(v=>v.Start).ToList());
  painting=true;
  visits.Items.Clear();foreach(Visit v in rows)visits.Items.Add(new Entry<Visit>{Value=v,Label=VisitLabel(v)});
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
  var v=SelectedVisit;if(v==null){LoadRecords();return;}
  Touch();vault.Save(s=>s.Audit(provider,"chart viewed",v.Id));LoadRecords();
 }
 void LoadRecords(){
  var visit=SelectedVisit;
  bool showVisit=visit!=null&&visit.CheckedIn!=null;
  visitHeading.Visible=visit!=null;sections.Visible=showVisit;detail.Visible=!showVisit;
  if(visit==null){detail.Text="Find and verify a patient to open an appointment.";LoadSection();return;}
  string heading=visit.CompletedAt!=null?"Completed appointment":showVisit?"Current visit":"Verified patient";
  visitHeading.Text=heading+"\r\n"+visit.Name+"  |  DOB "+visit.BirthDate+"  |  "+visit.Phone+"\r\n"+
   "Scheduled "+Common.Utc(visit.Start).ToLocalTime().ToString("g");
  if(!showVisit)detail.Text="Identity confirmed. Select Mark patient signed in to open the current visit.";
  if(visit.CompletedAt!=null){
   bool queued=vault.View(s=>s.Queue.Any(e=>e.VisitId==visit.Id));
   visitHeading.Text+="\r\n"+(queued?"Completed locally · chart handoff pending":"Completed · cloud adapter acknowledged");
  }
  LoadSection();
 }
 void LoadSection(){
  records.Items.Clear();visitDetail.Clear();
  var visit=SelectedVisit;int section=sections.SelectedIndex;
  if(visit==null||visit.CheckedIn==null||section<0)return;
  editItem.Text=section==0?"Add or edit chart entry":section==1?"Add or edit prescription":"Add or edit referral request";
  editItem.Visible=visit.CompletedAt==null;
  foreach(var item in visit.Records.Where(r=>section==2?r.Kind=="Referral"&&r.EncounterId==visit.Id:
       section==1?r.Kind=="Prescription":r.Kind!="Prescription"&&r.Kind!="Referral"))
   records.Items.Add(new Entry<Record>{Value=item,Label=item.Kind+"   ·   "+item.Title});
  visitDetail.Text=section==2?"Referral requests are included in the chart handoff.":
   section==1?"Select a prescription to review its details.":"Select a chart entry to review its details.";
 }
 void CheckInPatient(){
  var visit=SelectedVisit;
  if(visit==null){Say("Verify the patient before marking them signed in.");return;}
  if(visit.CompletedAt!=null){Say("This appointment is already complete.");return;}
  vault.Save(s=>s.CheckIn(VerifiedVisit(s,visit.Id),provider));
  sections.SelectedIndex=0;Populate();LoadRecords();Say("Patient signed in. Current visit opened.");
 }
 Visit VerifiedVisit(State state,string id){
  var current=state.Visits.Find(v=>v.Id==id);
  if(current==null||!IsVerified(current))throw new InvalidOperationException("Verify the patient again.");
  return current;
 }
 void ShowRecord(){
  var r=SelectedRecord;if(provider==null||r==null||DateTime.UtcNow>expires)return;
  Touch();vault.Save(s=>s.Audit(provider,"record viewed",r.Id));visitDetail.Text=r.Title+"\r\n\r\n"+r.Body;
 }
 static string PassHash(string code){
  using(var sha=SHA256.Create())return Convert.ToBase64String(sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(code)));
 }
 void Issue(){
  var v=SelectedVisit;if(v==null){Say("Select a visit.");return;}
  if(v.CompletedAt!=null){Say("This appointment is already complete.");return;}
  string code=Common.Secret(9).Replace('+','-').Replace('/','_');string issuer=provider;
  vault.Save(s=>{var x=VerifiedVisit(s,v.Id);if(x.CompletedAt!=null)throw new InvalidOperationException();x.PassHash=PassHash(code);x.PassUntil=DateTime.UtcNow.AddMinutes(15).ToString("o");s.Audit(issuer,"check-in code issued",v.Id);});
  MessageBox.Show(this,"Confirm the patient’s identity and give them this one-use code.\r\n\r\n"+code,"Patient access");
  Logout();
 }
 void PatientLogin(){
  string[] response=Prompt(this,"Patient check-in",new[]{"One-use check-in code"},null,0);if(response==null)return;
  string id=null;string hash=PassHash(response[0].Trim());
  vault.Save(s=>{
   var v=s.Visits.Find(x=>x.CompletedAt==null&&Common.Equal(hash,x.PassHash)&&Common.Utc(x.PassUntil)>DateTime.UtcNow);
   if(v==null){s.Audit("patient","check-in failed",null);return;}
   id=v.Id;v.PassHash=null;v.PassUntil=null;
   s.CheckIn(v,"patient:"+id);s.Audit("patient:"+id,"sign in",id);
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
 void RecordEditor(string kind){
  var v=SelectedVisit;if(v==null||v.CheckedIn==null||v.CompletedAt!=null){Say("Open a current visit first.");return;}
  var r=SelectedRecord;string actor=provider;
  var choice=r==null||r.Kind=="Signed form"?DialogResult.No:
   MessageBox.Show(this,"Edit the selected item? Choose No to add an item.","Visit entry",MessageBoxButtons.YesNoCancel);
  if(choice==DialogResult.Cancel)return;
  bool edit=choice==DialogResult.Yes;
  bool medication=kind=="prescription",referral=kind=="referral";
  var a=Prompt(this,medication?"Prescription documentation":referral?"Referral request":"Doctor chart entry",
   new[]{medication?"Medication":referral?"Receiving office or specialty":"Title",
    medication?"Dose, route, frequency and instructions":referral?"Reason, urgency and transfer instructions":"Chart text"},
   edit?new[]{r.Title,r.Body}:null,-1,1);
  if(a==null||a.Any(String.IsNullOrWhiteSpace)||provider!=actor||DateTime.UtcNow>expires)return;
  vault.Save(s=>{
   var current=VerifiedVisit(s,v.Id);
   if(edit){
    var latest=current.Records.Find(x=>x.Id==r.Id);
    if(latest==null||latest.Body!=r.Body||latest.Title!=r.Title||latest.Kind!=r.Kind)throw new InvalidOperationException();
   }
   s.Edit(current,actor,kind,new Record{Id=edit?r.Id:Common.Id(),
    Kind=medication?"Prescription":referral?"Referral":edit?r.Kind:"Clinical note",Title=a[0].Trim(),Body=a[1]});
  });
  Populate();LoadRecords();Say("Saved to the encrypted queue");
 }
 void CompleteVisit(){
  var visit=SelectedVisit;string actor=provider;
  if(visit==null||visit.CheckedIn==null){Say("Sign the patient in before completing the appointment.");return;}
  if(visit.CompletedAt!=null){Say("This appointment is already complete.");return;}
  if(MessageBox.Show(this,"Complete this appointment and queue the visit for the charting system? The visit will become read-only.",
   "Complete appointment",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes
   ||provider!=actor||DateTime.UtcNow>expires)return;
  vault.Save(s=>s.Complete(VerifiedVisit(s,visit.Id),actor));
  Populate();LoadRecords();Say("Appointment completed. Chart handoff queued.");
  var unused=RefreshCloud();
 }
 void AuditLog(){
  vault.Save(s=>s.Audit(provider,"audit viewed",null));
  var lines=vault.View(s=>s.Log.Skip(Math.Max(0,s.Log.Count-200)).Select(x=>x.At+"   "+x.Actor+"   "+x.Action+"   "+x.Target).ToArray());
  Prompt(this,"Workspace audit log",new[]{"Most recent audit events"},new[]{String.Join("\r\n",lines)},-1,0,0);
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
   var setup=vault.View(s=>new{s.Endpoint,s.Token,s.Connections,s.PracticeId,s.Offline});
   if(setup.Offline){Say("Reconnect before reviewing a conflict.");return;}
   ICloud target=CloudTarget(setup.PracticeId,setup.Connections,setup.Endpoint,setup.Token);
   var remote=await Task.Run(()=>{
    if(target.Verify().PracticeId!=setup.PracticeId)throw new InvalidOperationException();
    return target.Chart(v.Id);
   });
   if(provider!=actor||DateTime.UtcNow>expires)return;
   if(remote.Id!=v.Id||remote.PatientId!=v.PatientId||String.IsNullOrWhiteSpace(remote.Revision))throw new InvalidOperationException();
   vault.Save(s=>s.Audit(actor,"conflict chart reviewed",v.Id));
   var current=e.Record==null?null:remote.Records.Find(r=>r.Id==e.Record.Id);
   string cloud=e.Encounter!=null?remote.CompletedAt??"(appointment open)":e.Record==null?remote.CheckedIn??"(awaiting check-in)":
    current==null?"(no matching record)":current.Title+"\r\n"+current.Body;
   string local=e.Encounter!=null?"Complete appointment at "+e.At+" with "+e.Encounter.Records.Count+" visit entries.":
    e.Record==null?e.At:e.Record.Title+"\r\n"+e.Record.Body;
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
 void StartTraining(){
  if(busy){Say("Wait for the current sync.");return;}
  string actor=provider;
  if(MessageBox.Show(this,"Use only fictional information in this training workspace.","Start training",MessageBoxButtons.OKCancel)!=DialogResult.OK||
     actor!=provider||!admin)return;
  dummy.Seed();
  vault.Save(s=>{if(s.PracticeId!=null)throw new InvalidOperationException();s.PracticeId="demo";s.Audit(actor,"training linked","demo");});
  Draw();Say("Training clinic linked");
  var unused=RefreshCloud();
 }
 ICloud CloudTarget(string practice,ChartingConnections connections,string endpoint,string token){
  if(practice=="demo")return dummy;
  if(connections!=null)return new Remote(connections);
  return new Remote(endpoint,token);
 }
 async void Connect(){
  if(busy){Say("Wait for the current sync.");return;}
  var linked=vault.View(s=>new{s.PracticeId,s.Connections,s.Endpoint});
  if(linked.Connections==null&&linked.Endpoint!=null&&vault.View(s=>s.Queue.Count>0||s.Visits.Any(v=>v.Active))){
   Say("Complete active visits and sync the legacy queue before switching to three connections.");return;
  }
  string actor=provider;busy=true;
  try{
   using(var dialog=new ChartingConnectionDialog(linked.Connections,linked.Endpoint,linked.PracticeId)){
    if(dialog.ShowDialog(this)!=DialogResult.OK||actor!=provider||!admin||DateTime.UtcNow>expires)return;
    var configured=dialog.Connections;
    var identity=dialog.VerifiedIdentity;
    configured.RoutingId=identity.RoutingId;
    vault.Save(s=>{
     if(s.PracticeId!=linked.PracticeId||(s.Connections==null&&s.Endpoint!=null&&(s.Queue.Count>0||s.Visits.Any(v=>v.Active))))
      throw new InvalidOperationException();
     s.Connections=configured;s.PracticeId=identity.PracticeId;s.Endpoint=null;s.Token=null;
     s.Audit(actor,"charting connections verified",identity.PracticeId);
    });
   }
   Draw();Say("Three charting connections linked");
  }catch(Exception){Say("Could not save the charting connections. Previous settings are still in use.");}
  finally{busy=false;}
  await RefreshCloud();
 }
 async Task RefreshCloud(){
  if(busy)return;
  var cutoff=DateTime.UtcNow.AddHours(-24);
  if(vault.View(s=>s.Visits.Exists(v=>!v.Active&&Common.Utc(v.Start)<cutoff&&!s.Queue.Exists(e=>e.VisitId==v.Id))))
   vault.Save(s=>s.Visits.RemoveAll(v=>!v.Active&&Common.Utc(v.Start)<cutoff&&!s.Queue.Exists(e=>e.VisitId==v.Id)));
  var setup=vault.View(s=>new{s.Endpoint,s.Token,s.Connections,s.PracticeId,s.Offline});
  if(setup.PracticeId==null){connectionStatus="Cloud setup required";Status();return;}
  if(setup.Offline){connectionStatus="Simulated outage";Status();return;}
  busy=true;
  try{
   ICloud target=CloudTarget(setup.PracticeId,setup.Connections,setup.Endpoint,setup.Token);
   if(setup.PracticeId=="demo"&&!demoSeeded){
    await Task.Run(()=>dummy.Seed());demoSeeded=true;
   }
   await Task.Run(()=>sync.Run(target,setup.PracticeId));connectionStatus="Connected";
   if(provider!=null&&Form.ActiveForm==this)Populate();
  }catch(Exception){connectionStatus="Sync unavailable · retrying automatically";}
  finally{busy=false;Status();}
 }
}
sealed class ChartingConnectionDialog : Form {
 readonly TextBox[] urls=new TextBox[3],keys=new TextBox[3],secrets=new TextBox[3];
 readonly ConnectionKey[] saved;
 readonly TabControl tabs=new TabControl{Dock=DockStyle.Fill};
 readonly Label status=new Label{Dock=DockStyle.Top,Height=68,Padding=new Padding(12,8,12,0)};
 readonly Button verify=new Button{Text="Verify and link",AutoSize=true};
 public ChartingConnections Connections {get;private set;}
 public Identity VerifiedIdentity {get;private set;}

 public ChartingConnectionDialog(ChartingConnections previous,string legacyEndpoint,string practice){
  Text="Charting connections";Width=690;Height=445;StartPosition=FormStartPosition.CenterParent;
  MinimizeBox=false;MaximizeBox=false;Font=new Font("Segoe UI",10);
  saved=previous==null?new ConnectionKey[3]:new[]{previous.Appointments,previous.Charts,previous.Writeback};
  string[] names={"Appointments","Chart reads","Chart writeback"};
  string[] purposes={"Find appointments starting in the next 24 hours.","Download charts for the scheduled patients.",
   "Send queued updates and completed visits to the charting system."};
  for(int i=0;i<names.Length;i++){
   var tab=new TabPage(names[i]);
   var fields=new TableLayoutPanel{Dock=DockStyle.Fill,ColumnCount=1,AutoScroll=true,Padding=new Padding(14)};
   fields.Controls.Add(new Label{Text=purposes[i],AutoSize=true,Margin=new Padding(3,3,3,13)});
   urls[i]=Field(fields,"HTTPS adapter base URL",saved[i]==null?legacyEndpoint??"":saved[i].Url,false,2048);
   keys[i]=Field(fields,"API key ID","",false,4096);
   secrets[i]=Field(fields,"API key secret","",true,4096);
   if(saved[i]!=null)fields.Controls.Add(new Label{Text="Leave both key fields blank to keep the saved pair for this URL.",AutoSize=true});
   tab.Controls.Add(fields);tabs.TabPages.Add(tab);
  }
  bool training=practice=="demo";
  status.Text=training?"Training workspace. You can verify connections here. Use a separate practice workspace to link them.":
   "Enter a separate key pair on each tab. All three connections must identify the same practice.";
  if(training)verify.Text="Verify connections";
  var bar=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=50,Padding=new Padding(10,4,0,0)};
  var cancel=new Button{Text="Cancel",DialogResult=DialogResult.Cancel,AutoSize=true};
  bar.Controls.Add(verify);bar.Controls.Add(cancel);Controls.Add(tabs);Controls.Add(bar);Controls.Add(status);
  AcceptButton=verify;CancelButton=cancel;
  verify.Click+=async(s,e)=>await VerifyConnections(previous,practice,training);
  var timeout=new Timer{Interval=180000};timeout.Tick+=(s,e)=>Close();timeout.Start();
  FormClosed+=(s,e)=>timeout.Dispose();
 }
 static TextBox Field(TableLayoutPanel panel,string label,string value,bool mask,int maxLength){
  panel.Controls.Add(new Label{Text=label,AutoSize=true});
  var box=new TextBox{Dock=DockStyle.Top,Text=value,MaxLength=maxLength,UseSystemPasswordChar=mask,AccessibleName=label};
  panel.Controls.Add(box);return box;
 }
 ConnectionKey Read(int i){
  try{
   string url=ConnectionKey.NormalizeUrl(urls[i].Text.Trim());
   var existing=saved[i];
   if(existing!=null&&url==existing.Url&&keys[i].Text.Length==0&&secrets[i].Text.Length==0)return existing;
   var candidate=new ConnectionKey{Url=url,KeyId=keys[i].Text.Trim(),Secret=secrets[i].Text.Trim()};
   candidate.Validate();return candidate;
  }catch(ArgumentException){tabs.SelectedIndex=i;throw;}
 }
 async Task VerifyConnections(ChartingConnections previous,string practice,bool training){
  verify.Enabled=false;tabs.Enabled=false;
  try{
   var configured=new ChartingConnections{Appointments=Read(0),Charts=Read(1),Writeback=Read(2),
    RoutingId=previous==null?null:previous.RoutingId};
   var adapter=new Remote(configured);
   status.Text="Verifying the three connections…";
   var identity=await Task.Run(()=>adapter.Verify());
   if(IsDisposed)return;
   if(identity.PracticeId=="demo"||(!training&&practice!=null&&identity.PracticeId!=practice))
    throw new IOException("The verified connections belong to a different practice.");
   if(training){status.Text="Verified all three connections for "+identity.Practice+". Use a separate practice workspace to link them.";return;}
   if(MessageBox.Show(this,"Link appointments, chart reads, and chart writeback for "+identity.Practice+" ("+identity.PracticeId+")?",
     "Confirm charting connections",MessageBoxButtons.YesNo,MessageBoxIcon.Question,MessageBoxDefaultButton.Button2)!=DialogResult.Yes){
    status.Text="Connections verified. Review the settings before linking.";return;
   }
   if(IsDisposed)return;
   Connections=configured;VerifiedIdentity=identity;DialogResult=DialogResult.OK;
  }catch(ArgumentException ex){if(!IsDisposed)status.Text=ex.Message;}
   catch(IOException ex){if(!IsDisposed)status.Text=ex.Message;}
   catch(Exception){if(!IsDisposed)status.Text="Verification failed. Check the three adapter URLs and key pairs.";}
  finally{if(!IsDisposed){verify.Enabled=true;tabs.Enabled=true;}}
 }
}
}
