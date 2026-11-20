using System;
using System.IO;
using System.Drawing;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using MedQueue;

static class Setup {
 [STAThread]
 static int Main(string[] args){
  Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
  using(var window=new Form{Text="MedQueue Setup",Width=690,Height=365,StartPosition=FormStartPosition.CenterScreen,Font=new Font("Segoe UI",11),FormBorderStyle=FormBorderStyle.FixedDialog,MaximizeBox=false}){
   var panel=new FlowLayoutPanel{Dock=DockStyle.Fill,Padding=new Padding(24),FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true};
   panel.Controls.Add(new Label{Text="Install MedQueue",AutoSize=true,Font=new Font("Segoe UI",20,FontStyle.Bold)});
   panel.Controls.Add(new Label{Text="Unlock the BitLocker volume in File Explorer before installing.",AutoSize=true,Margin=new Padding(3,15,3,12)});
   var drives=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=250};
   foreach(DriveInfo drive in DriveInfo.GetDrives())if(drive.DriveType==DriveType.Fixed)drives.Items.Add(drive.Name);
   if(drives.Items.Count>0)drives.SelectedIndex=0;panel.Controls.Add(drives);
   var name=new TextBox{Text="MedQueue",Width=250,MaxLength=48};
   panel.Controls.Add(new Label{Text="Workspace folder name",AutoSize=true});panel.Controls.Add(name);
   panel.Controls.Add(new Label{Text="Windows may ask for administrator approval to check BitLocker.",AutoSize=true});
   var install=new Button{Text="Verify drive and install",AutoSize=true};panel.Controls.Add(install);
   install.Click+=(s,e)=>{
    try{
     if(drives.SelectedItem==null||!System.Text.RegularExpressions.Regex.IsMatch(name.Text,@"^MedQueue(?:-[a-zA-Z0-9][a-zA-Z0-9-]{0,37})?$"))throw new ArgumentException("Use MedQueue or MedQueue followed by a hyphen and a workspace name.");
     string root=(string)drives.SelectedItem;
     Common.CheckDrive(root);
     string folder=Path.Combine(root,name.Text);
     Common.PrivateFolder(folder);
     Directory.CreateDirectory(Path.Combine(folder,"training"));
     string exe=Path.Combine(folder,"MedQueue.exe");
     using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("MedQueuePayload"))
     using(var destination=new FileStream(exe,FileMode.CreateNew,FileAccess.Write)){stream.CopyTo(destination);destination.Flush(true);}
     try{Shortcut(exe,folder,name.Text);}catch{MessageBox.Show(window,"Installed. Open "+exe+" to start MedQueue.","MedQueue");window.Close();return;}
     MessageBox.Show(window,"Installed to "+folder+". Open "+name.Text+" from the Start menu.","MedQueue");
     window.Close();
    }catch(UnauthorizedAccessException){MessageBox.Show(window,"Windows denied permission to create or write the workspace. Sign in to the intended Windows account and ask IT to grant it permission to create a folder on the selected volume.","MedQueue");}
     catch(Exception ex){MessageBox.Show(window,ex.Message,"MedQueue");}
   };
   window.Controls.Add(panel);Application.Run(window);return 0;
  }
 }
 static void Shortcut(string executable,string folder,string name){
  Type shellType=Type.GetTypeFromProgID("WScript.Shell");dynamic shell=Activator.CreateInstance(shellType);dynamic shortcut=null;
  try{
   shortcut=shell.CreateShortcut(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs),name+".lnk"));
   shortcut.TargetPath=executable;shortcut.WorkingDirectory=folder;shortcut.Description="MedQueue downtime workspace";shortcut.Save();
  }finally{if(shortcut!=null)Marshal.FinalReleaseComObject(shortcut);Marshal.FinalReleaseComObject(shell);}
 }
}
