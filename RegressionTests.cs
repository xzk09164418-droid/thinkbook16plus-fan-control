using System;
using System.IO;
using System.Reflection;
using System.Windows.Forms;
using ThinkBookFanCurve;
static class RegressionTests {
 [STAThread] static int Main() {
  try { return Run(); }
  catch(Exception error) { PrintFailure(error); return 1; }
 }
 static void PrintFailure(Exception error) {
  // Some machines fail while formatting Exception.ToString(); keep diagnostics usable.
  Console.Error.WriteLine(error.GetType().FullName + ": " + error.Message);
  if(error.InnerException != null) PrintFailure(error.InnerException);
 }
 static int Run() {
  if(File.Exists(Files.Marker))throw new Exception("Run tests in an isolated directory; production recovery marker exists.");
  int result=(int)typeof(MainForm).Assembly.GetType("ThinkBookFanCurve.Program").GetMethod("Main",BindingFlags.NonPublic|BindingFlags.Static).Invoke(null,new object[]{new string[]{"--self-test"}});
  if(result!=0)throw new Exception("Temperature test failed: "+result);
  var monitor=new RpmDeviationMonitor();
  Func<double,double,int,int,bool> observe=(now,age,fan1,fan2)=>monitor.Observe(now,age,3500,3800,fan1,fan2);
  if(observe(10,10,1500,1900) || monitor.Count!=0)throw new Exception("Settling allowance failed");
  if(observe(12,12,1500,1900) || observe(14,14,1500,1900) || !observe(16,16,1500,1900))throw new Exception("Consecutive deviation not detected");
  monitor.Reset();
  if(observe(20,20,1500,1900) || observe(22,22,3500,3800) || monitor.Count!=0 || observe(24,24,1500,1900))throw new Exception("Transient recovery failed");
  monitor.Reset();
  if(observe(30,30,2700,3000) || monitor.Count!=0)throw new Exception("800 RPM boundary failed");
  if(observe(32,32,3500,2900) || observe(34,34,3500,2900) || !observe(36,36,3500,2900))throw new Exception("Fan2-only deviation failed");
  monitor.Reset();
  observe(40,40,1500,1900);observe(42,42,1500,1900);
  if(observe(50,50,1500,1900) || monitor.Count!=1)throw new Exception("Sampling gap should reset count");
  monitor.Reset();
  observe(60,60,1500,1900);
  if(observe(60,60,1500,1900) || monitor.Count!=1)throw new Exception("Duplicate timestamp counted");
  if(observe(61,61,1500,1900) || observe(62,62,1500,1900))throw new Exception("Minimum duration ignored");
  if(!observe(64,64,1500,1900))throw new Exception("Sustained deviation missed");
  monitor.Reset();
  observe(70,70,1500,1900);observe(72,72,1500,1900);
  if(observe(74,0,1500,1900) || monitor.Count!=0)throw new Exception("New target settling should reset count");
  var throttle=new RecoveryThrottle();int restarts=0;
  if(!throttle.TryRun(0,()=>restarts++) || throttle.TryRun(59,()=>restarts++) || !throttle.TryRun(60,()=>restarts++) || restarts!=2)throw new Exception("Recovery throttle failed");
  throttle.Reset();try {throttle.TryRun(0,()=>{throw new Exception("restore failed");});}catch{}
  if(!throttle.TryRun(1,()=>restarts++))throw new Exception("Failed restore consumed retry");
  var startup=new StartupTransition();var config=CurveConfig.Default();config.Offset=0;
  var sample=new Snapshot {Cpu=60,Gpu=40,Fan1=2000,Fan2=2300};
  startup.Begin(0);
  for(int i=0;i<30;i+=2)if(startup.Update(i,sample,config)!=null)throw new Exception("Wrote target during read-only sampling");
  var first=startup.Update(30,sample,config);
  if(first[0]!=2000 || first[1]!=2300 || startup.Count!=15 || startup.CpuMean!=60 || startup.Fan1Mean!=2000)throw new Exception("Baseline mean failed");
  var half=startup.Update(60,sample,config);
  if(half[0]!=2400 || half[1]!=2700)throw new Exception("Linear ramp midpoint failed");
  sample.Cpu=70;
  var end=startup.Update(90,sample,config);
  if(end[0]!=2800 || end[1]!=3100 || startup.Phase!=StartupTransition.Stage.Complete)throw new Exception("Fixed endpoint or completion failed");
  startup.Begin(100);startup.Update(100,sample,config);startup.Update(108,sample,config);
  if(startup.Started!=108 || startup.Count!=1)throw new Exception("Sampling gap did not reset window");
  startup.Cancel();if(startup.Update(150,sample,config)!=null)throw new Exception("Cancelled startup resumed");
  startup.Begin(200);sample.Fan1=sample.Fan2=0;
  for(int i=0;i<30;i+=2)startup.Update(200+i,sample,config);
  var clamped=startup.Update(230,sample,config);
  if(clamped[0]!=1500 || clamped[1]!=1900)throw new Exception("Firmware minimum clamp failed");
  startup.Begin(300);sample.Fan1=4000;sample.Fan2=4400;sample.Cpu=60;
  for(int i=0;i<30;i+=2)startup.Update(300+i,sample,config);
  startup.Update(330,sample,config);var decreasing=startup.Update(360,sample,config);
  if(decreasing[0]!=3400 || decreasing[1]!=3800)throw new Exception("Downward ramp failed");
  var heat=new ThermalOverride();int takeovers=0;Action takeover=()=>takeovers++;
  if(heat.Run(0,81,73,takeover) || takeovers!=0)throw new Exception("Thermal threshold too early");
  if(!heat.Run(1,82,40,takeover) || takeovers!=1)throw new Exception("CPU thermal threshold missed");
  heat.Run(3,90,40,takeover);if(takeovers!=1)throw new Exception("Thermal loop too frequent");
  heat.Run(7,90,40,takeover);if(takeovers!=2)throw new Exception("Unchanged max target not reapplied");
  heat.Run(8,78,70,takeover);if(!heat.Run(22,78,70,takeover) || heat.Run(23,78,70,takeover))throw new Exception("Thermal cool-down hysteresis failed");
  if(!heat.Run(24,50,74,takeover))throw new Exception("GPU thermal threshold missed");
  heat.Reset();if(heat.Run(25,50,40,takeover))throw new Exception("Manual cancellation failed");
  heat.Reset();startup.Begin(0);
  if(!heat.Run(0,82,40,()=>{startup.Cancel();takeover();}) || startup.Phase!=StartupTransition.Stage.Complete)throw new Exception("Thermal takeover did not bypass sampling");
  heat.Reset();try {heat.Run(0,82,40,()=>{throw new Exception("write failure");});}catch{}
  int beforeRetry=takeovers;heat.Run(1,82,40,takeover);if(takeovers!=beforeRetry+1)throw new Exception("Failed takeover suppressed retry");
  var schedule=new DailyControlSchedule();int pauses=0,resumes=0;
  var midnight=new DateTimeOffset(2026,9,16,0,0,0,TimeSpan.FromHours(8));
  if(DailyControlSchedule.IsQuiet(midnight.AddSeconds(-1)) || !DailyControlSchedule.IsQuiet(midnight) || !DailyControlSchedule.IsQuiet(midnight.AddHours(10).AddTicks(-1)) || DailyControlSchedule.IsQuiet(midnight.AddHours(10)))throw new Exception("Quiet-hour boundaries failed");
  if(!DailyControlSchedule.IsQuiet(midnight.ToUniversalTime()))throw new Exception("UTC+8 conversion failed");
  if(schedule.Check(midnight,true,()=>pauses++,()=>resumes++) || !schedule.Paused || pauses!=1)throw new Exception("Midnight release failed");
  schedule.Check(midnight.AddHours(2),false,()=>pauses++,()=>resumes++);
  if(pauses!=1 || resumes!=0)throw new Exception("Repeated night hardware action");
  if(!schedule.Check(midnight.AddHours(10),false,()=>pauses++,()=>resumes++) || schedule.Paused || resumes!=1)throw new Exception("Morning resume failed");
  schedule.Check(midnight,true,()=>pauses++,()=>resumes++);schedule.Cancel();
  schedule.Check(midnight.AddHours(10),false,()=>pauses++,()=>resumes++);if(resumes!=1)throw new Exception("Manual stop should cancel morning resume");
  var idleSchedule=new DailyControlSchedule();idleSchedule.Check(midnight,false,()=>pauses++,()=>resumes++);
  idleSchedule.Check(midnight.AddHours(10),false,()=>pauses++,()=>resumes++);if(resumes!=1)throw new Exception("Monitoring-only app unexpectedly resumed");
  schedule.Cancel();try {schedule.Check(midnight,true,()=>{throw new Exception("restore failure");},()=>resumes++);}catch{}
  if(schedule.Paused)throw new Exception("Failed restore incorrectly marked successful pause");
  Application.EnableVisualStyles();
  if(GpuTemperature.Parse(" 45\r\n61\n")!=61)throw new Exception("GPU parser failed");
  foreach(string invalidGpu in new [] {"","N/A","0","106","45\nN/A"}) {
   bool rejectedGpu=false;try {GpuTemperature.Parse(invalidGpu);}catch(GpuTemperatureException) {rejectedGpu=true;}
   if(!rejectedGpu)throw new Exception("GPU parser accepted invalid temperature");
  }
  var gpuRetry=new GpuRecovery();
  gpuRetry.Fail(0,true);
  if(!gpuRetry.Waiting || !gpuRetry.ResumeRequested || gpuRetry.Due(9) || gpuRetry.NextAttempt!=10)throw new Exception("GPU retry setup failed");
  gpuRetry.Fail(10,false);if(gpuRetry.NextAttempt!=30 || !gpuRetry.ResumeRequested)throw new Exception("GPU intent lost after second failure");
  gpuRetry.Fail(30,false);if(gpuRetry.NextAttempt!=70)throw new Exception("GPU 40-second retry failed");
  gpuRetry.Fail(70,false);gpuRetry.Fail(130,false);if(gpuRetry.NextAttempt!=190)throw new Exception("GPU retry cap failed");
  if(gpuRetry.Healthy(189) || gpuRetry.Healthy(190) || gpuRetry.Healthy(190) || gpuRetry.Healthy(192) || !gpuRetry.Healthy(194))throw new Exception("GPU stable recovery gating failed");
  gpuRetry.Cancel();gpuRetry.Fail(200,false);
  if(gpuRetry.ResumeRequested || gpuRetry.Healthy(210) || gpuRetry.Healthy(212))throw new Exception("GPU monitor-only recovery failed");
  gpuRetry.Fail(214,false);if(gpuRetry.HealthyCount!=0 || gpuRetry.ResumeRequested)throw new Exception("GPU failure did not clear healthy streak");
  gpuRetry.Healthy(234);gpuRetry.Healthy(236);
  if(gpuRetry.Healthy(244) || gpuRetry.HealthyCount!=1)throw new Exception("GPU recovery accepted stale readings");
  gpuRetry.Cancel();if(gpuRetry.Waiting || gpuRetry.ResumeRequested || gpuRetry.Healthy(300))throw new Exception("GPU cancellation failed");
  Application.ThreadException+=(sender,error)=> {PrintFailure(error.Exception);Environment.Exit(1);};
  var battery=new BatteryPause();int releases=0,powerResumes=0;
  if(battery.Check(false,true,()=>releases++,()=>powerResumes++) || !battery.Paused || !battery.ResumeRequested || releases!=1)throw new Exception("Battery release failed");
  battery.Check(false,false,()=>releases++,()=>powerResumes++);
  if(releases!=1 || powerResumes!=0)throw new Exception("Battery repeated hardware operation");
  if(!battery.Check(true,false,()=>releases++,()=>powerResumes++) || battery.Paused || powerResumes!=1)throw new Exception("AC resume failed");
  battery.Check(true,true,()=>releases++,()=>powerResumes++);if(powerResumes!=1)throw new Exception("Repeated AC resume");
  battery.Check(false,false,()=>releases++,()=>powerResumes++);
  battery.Check(true,false,()=>releases++,()=>powerResumes++);if(powerResumes!=1)throw new Exception("Monitor-only acquired control");
  battery.Check(false,true,()=>releases++,()=>powerResumes++);battery.CancelResume();
  battery.Check(true,false,()=>releases++,()=>powerResumes++);if(powerResumes!=1)throw new Exception("Manual cancellation lost");
  try {battery.Check(false,true,()=>{throw new Exception("restore failed");},()=>powerResumes++);}catch{}
  if(!battery.Paused || !battery.ResumeRequested)throw new Exception("Failed release lost intent");
  int priorReleases=releases;battery.Check(true,false,()=>releases++,()=>powerResumes++);
  if(releases!=priorReleases+1 || powerResumes!=2)throw new Exception("Release retry before resume failed");
  using(var pulse=new System.Threading.EventWaitHandle(false,System.Threading.EventResetMode.AutoReset))
  using(var parentExit=new System.Threading.EventWaitHandle(false,System.Threading.EventResetMode.ManualReset))
  using(var stopEvent=new System.Threading.EventWaitHandle(false,System.Threading.EventResetMode.ManualReset))
  using(var arm=new System.Threading.EventWaitHandle(false,System.Threading.EventResetMode.ManualReset)) {
   // Reproduce pulse-consumed-before-arm: no new pulse is sent after this point.
   pulse.Set();if(!pulse.WaitOne(0))throw new Exception("Pulse setup failed");
   int signal=-1;
   var waiter=new System.Threading.Thread(()=>signal=Guard.WaitForActivity(false,pulse,parentExit,stopEvent,arm));
   waiter.IsBackground=true;waiter.Start();arm.Set();
   if(!waiter.Join(2000) || signal!=3)throw new Exception("Idle watchdog lost the arming wakeup");
   arm.Reset();stopEvent.Set();
   if(Guard.WaitForActivity(false,pulse,parentExit,stopEvent,arm)!=2)throw new Exception("Idle watchdog did not stop");
   stopEvent.Reset();parentExit.Set();
   if(Guard.WaitForActivity(false,pulse,parentExit,stopEvent,arm)!=1)throw new Exception("Idle watchdog missed parent exit");
  }
  var floor=new SamplingMinimum();var writes=new System.Collections.Generic.List<string>();
  Action<int,int> write=(fan,rpm)=>writes.Add(fan+":"+rpm);
  floor.Apply(0,1500,1600,write);if(writes.Count!=0)throw new Exception("Floor lowered a healthy fan");
  floor.Apply(1,1499,0,write);
  if(String.Join(",",writes)!="0:1500,1:1900" || !floor.Applied)throw new Exception("Sampling floor target failed");
  floor.Apply(3,0,0,write);if(writes.Count!=2)throw new Exception("Floor retry too frequent");
  floor.Apply(7,0,2000,write);if(writes.Count!=3 || writes[2]!="0:1500")throw new Exception("Independent floor retry failed");
  floor.Reset();if(floor.Applied)throw new Exception("Floor reset failed");
  try {floor.Apply(8,0,2000,(fan,rpm)=>{throw new Exception("write failure");});}catch{}
  floor.Apply(9,0,2000,write);if(writes.Count!=4)throw new Exception("Failed floor write suppressed retry");
  using(var form=new MainForm(true,true)) {
   // Exercise the real message loop and visibility without initializing hardware.
   typeof(MainForm).GetField("sessionStarted",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(form,true);
   using(var timer=new Timer {Interval=100}) {
    timer.Tick+=(s,e)=> {
     timer.Stop();
     if(form.Visible || form.ShowInTaskbar)throw new Exception("Background startup exposed window");
     typeof(MainForm).GetMethod("OpenWindow",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,null);
     if(!form.Visible || !form.ShowInTaskbar || form.WindowState!=FormWindowState.Normal)throw new Exception("Tray open failed");
     var flags=BindingFlags.NonPublic|BindingFlags.Instance;
     Action<string,object> set=(name,value)=>typeof(MainForm).GetField(name,flags).SetValue(form,value);
     Func<string,object> get=name=>typeof(MainForm).GetField(name,flags).GetValue(form);
     Action<Microsoft.Win32.PowerModes> power=mode=>typeof(MainForm).GetMethod("HandlePowerChange",flags).Invoke(form,new object[]{mode});
     Action checkPower=()=>typeof(MainForm).GetMethod("CheckPower",flags).Invoke(form,null);
     PowerLineStatus line=PowerLineStatus.Offline;form.ReadPowerStatus=()=>line;
     set("pendingAuto",true);
     typeof(MainForm).GetMethod("HandleGpuFailure",flags).Invoke(form,new object[]{new GpuTemperatureException("simulated GPU timeout")});
     var formGpuRetry=(GpuRecovery)get("gpuRecovery");
     if(!formGpuRetry.Waiting || !formGpuRetry.ResumeRequested || (bool)get("pendingAuto"))throw new Exception("GPU startup failure lost auto-enable intent");
     checkPower();
     if(formGpuRetry.Waiting || !((BatteryPause)get("battery")).ResumeRequested)throw new Exception("GPU retry was not transferred to battery pause");
     line=PowerLineStatus.Online;checkPower();
     typeof(MainForm).GetMethod("HandleGpuFailure",flags).Invoke(form,new object[]{new GpuTemperatureException("simulated repeat")});
     typeof(Button).GetMethod("OnClick",flags).Invoke(get("stop"),new object[]{EventArgs.Empty});
     if(formGpuRetry.Waiting || formGpuRetry.ResumeRequested || (bool)get("pendingAuto"))throw new Exception("Manual stop did not cancel GPU auto-enable");
     line=PowerLineStatus.Offline;
     set("pendingAuto",true);
     power(Microsoft.Win32.PowerModes.Suspend);power(Microsoft.Win32.PowerModes.Suspend);
     if(!(bool)get("suspended") || !(bool)get("resumeAfterSleep"))throw new Exception("Suspend lost startup intent");
     power(Microsoft.Win32.PowerModes.StatusChange);
     if(get("controller")!=null)throw new Exception("Suspend initialized hardware");
     power(Microsoft.Win32.PowerModes.Resume);
     var formBattery=(BatteryPause)get("battery");var pollTimer=(Timer)get("timer");
     if(!formBattery.Paused || !formBattery.ResumeRequested || pollTimer.Interval!=3600000 || get("controller")!=null || get("guard")!=null)throw new Exception("Battery wake did not stay power-only");
     typeof(Button).GetMethod("OnClick",flags).Invoke(get("stop"),new object[]{EventArgs.Empty});
     if(formBattery.ResumeRequested || (bool)get("pendingAuto"))throw new Exception("Battery cancellation failed before hardware initialization");
     line=PowerLineStatus.Online;checkPower();
     if((bool)get("pendingAuto") || pollTimer.Interval!=2000)throw new Exception("Monitor-only AC resume changed intent");
     line=PowerLineStatus.Offline;set("pendingAuto",true);checkPower();
     line=PowerLineStatus.Online;checkPower();
     if(!(bool)get("pendingAuto") || ((DataGridView)get("grid")).Enabled)throw new Exception("Pending AC startup did not lock configuration");
     set("pendingAuto",false);pollTimer.Stop();
     typeof(MainForm).GetMethod("HideWindow",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,null);
     if(form.Visible || form.ShowInTaskbar)throw new Exception("Tray hide failed");
     form.Close();
     if(form.IsDisposed || form.Visible || form.ShowInTaskbar)throw new Exception("Close did not keep app in tray");
     typeof(MainForm).GetMethod("OpenWindow",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,null);
     typeof(MainForm).GetField("busy",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(form,true);
     form.Close();
     if(form.IsDisposed || form.Visible)throw new Exception("Busy close did not hide window");
     typeof(MainForm).GetMethod("RequestExit",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,null);
     if(form.IsDisposed)throw new Exception("Exit did not wait for hardware read");
     typeof(MainForm).GetField("busy",BindingFlags.NonPublic|BindingFlags.Instance).SetValue(form,false);
     typeof(MainForm).GetMethod("Poll",BindingFlags.NonPublic|BindingFlags.Instance).Invoke(form,null);
     if(!form.IsDisposed)throw new Exception("Deferred exit did not complete");
    };
    using(var context=new TrayApplicationContext(form)) {
     if(context.MainForm!=null)throw new Exception("Tray startup assigned a visible main form");
     timer.Start();Application.Run(context);
    }
   }
  }
  using(var shutdownForm=new MainForm(true,true)) {
   var flags=BindingFlags.NonPublic|BindingFlags.Instance;
   typeof(MainForm).GetField("busy",flags).SetValue(shutdownForm,true);
   var closingArgs=new FormClosingEventArgs(CloseReason.WindowsShutDown,false);
   typeof(Form).GetMethod("OnFormClosing",flags).Invoke(shutdownForm,new object[]{closingArgs});
   if(closingArgs.Cancel)throw new Exception("Hardware read blocked Windows shutdown");
   typeof(MainForm).GetField("busy",flags).SetValue(shutdownForm,false);
   typeof(MainForm).GetMethod("RequestExit",flags).Invoke(shutdownForm,null);
  }
  File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"regression-test.txt"),"PASS: GPU valid/invalid parser, retry 10/20/40/60-second cap, preserved resume intent, three fresh consecutive snapshots, duplicate/gap/failure reset, monitor-only behavior, GPU retry transfer to battery pause, manual cancellation; watchdog pulse-before-arm wakeup, idle stop/parent exit; serialized suspend/wake, duplicate suspend, battery-only wake with no hardware initialization, hourly fallback, cancellation before hardware initialization, AC resume locking, deferred exit completion, Windows shutdown during read; battery release, repeated notifications, monitor-only resume, cancellation and failed-release retry; sampling floor 1500/1900, independent retry, failed-write retry/reset; quiet-hour boundaries and cancellation; CPU82/GPU74 thermal takeover, sampling bypass and hysteresis; 30s baseline, 60s ramp and gap reset; deviation detection and restart cooldown; rolling average and boost suppression; real tray message loop and open/hide/exit. No hardware writes.");
  return 0;
 }
}
