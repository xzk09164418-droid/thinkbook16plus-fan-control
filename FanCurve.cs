using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Management;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using System.Windows.Forms.DataVisualization.Charting;
using System.Web.Script.Serialization;

namespace ThinkBookFanCurve {
    public sealed class PointConfig {
        public double Temperature {get;set;}
        public int Fan1 {get;set;}
        public int Fan2 {get;set;}
    }
    public sealed class CurveConfig {
        public int Offset {get;set;}
        public List<PointConfig> Points {get;set;}
        public const int CpuFullSpeed=82, GpuFullSpeed=74;
        public static bool IsFullSpeed(double cpu,double gpu) {return cpu>=CpuFullSpeed || gpu>=GpuFullSpeed;}
        public static CurveConfig Default() {
            return new CurveConfig {Offset=5, Points=new List<PointConfig> {
                new PointConfig {Temperature=30, Fan1=2000, Fan2=2400},
                new PointConfig {Temperature=40, Fan1=2300, Fan2=2700},
                new PointConfig {Temperature=50, Fan1=2600, Fan2=2900},
                new PointConfig {Temperature=60, Fan1=2800, Fan2=3100},
                new PointConfig {Temperature=70, Fan1=3600, Fan2=3900},
                new PointConfig {Temperature=80, Fan1=4200, Fan2=4500},
                new PointConfig {Temperature=90, Fan1=4400, Fan2=4700},
                new PointConfig {Temperature=100, Fan1=4500, Fan2=4800},
                new PointConfig {Temperature=110, Fan1=4500, Fan2=4800}
            }};
        }
        public void Validate() {
            if(Offset<0 || Offset>15 || Points==null || Points.Count!=9)
                throw new Exception("偏移需为 0–15°C，曲线需包含 30–110°C 的 9 个节点。");
            int previous1=0,previous2=0;
            for(int i=0;i<Points.Count;i++) {
                PointConfig p=Points[i];
                if(p==null || p.Temperature!=30+10*i)throw new Exception("节点温度固定为 30–110°C，每 10°C 一个。");
                if(p.Fan1<1500 || p.Fan1>4500 || p.Fan2<1900 || p.Fan2>4800 || p.Fan1<previous1 || p.Fan2<previous2)
                    throw new Exception("转速需随温度不下降。风扇 1：1500–4500；风扇 2：1900–4800 RPM。");
                if(p.Temperature>=100 && (p.Fan1!=4500 || p.Fan2!=4800))throw new Exception("100°C 和 110°C 节点需为 4500 / 4800 RPM。");
                previous1=p.Fan1;previous2=p.Fan2;
            }
        }
        public int[] Target(double cpu, double gpu) {
            if (IsFullSpeed(cpu,gpu)) return new [] {4500,4800};
            double t=Math.Max(cpu,gpu)+Offset;
            PointConfig low=Points[0], high=Points[0];
            if (t>=Points[Points.Count-1].Temperature) low=high=Points[Points.Count-1];
            else {
                for(int i=1;i<Points.Count;i++) {
                    if(t<=Points[i].Temperature) {low=Points[i-1];high=Points[i];break;}
                }
            }
            double f=low==high?0:Math.Max(0,Math.Min(1,(t-low.Temperature)/(high.Temperature-low.Temperature)));
            // Reserve full speed for the unshifted CPU/GPU thresholds; offset and rounding cannot trigger it early.
            return new [] {Math.Min(4400,(int)(Math.Ceiling((low.Fan1+(high.Fan1-low.Fan1)*f)/100)*100)),
                          Math.Min(4700,(int)(Math.Ceiling((low.Fan2+(high.Fan2-low.Fan2)*f)/100)*100))};
        }
    }
    public sealed class Snapshot {
        public int Cpu, Gpu, Fan1, Fan2;
        public DateTime At;
    }
    // Timestamped rolling samples; long sampling gaps discard stale history.
    public sealed class TemperatureAverage {
        readonly Queue<Snapshot> samples=new Queue<Snapshot>();
        public double Cpu {get;private set;}
        public double Gpu {get;private set;}
        DateTime last=DateTime.MinValue;
        public void Clear() {samples.Clear();last=DateTime.MinValue;Cpu=Gpu=0;}
        public void Add(Snapshot sample) {
            if(last!=DateTime.MinValue && (sample.At<last || (sample.At-last).TotalSeconds>15)) Clear();
            if(sample.At==last)return;
            last=sample.At;
            samples.Enqueue(sample);
            while(samples.Count>0 && (sample.At-samples.Peek().At).TotalSeconds>=180) samples.Dequeue();
            Cpu=samples.Average(x=>(double)x.Cpu);Gpu=samples.Average(x=>(double)x.Gpu);
        }
        public int[] Target(CurveConfig config,Snapshot current) {
            Add(current);
            return CurveConfig.IsFullSpeed(current.Cpu,current.Gpu) ? new [] {4500,4800} : config.Target(Cpu,Gpu);
        }
    }
    public sealed class GpuTemperatureException : Exception {
        public GpuTemperatureException(string message,Exception inner=null):base(message,inner) {}
    }
    public static class GpuTemperature {
        public static int Parse(string output) {
            int gpu=-1;
            foreach(string line in output.Split(new [] {'\r','\n'},StringSplitOptions.RemoveEmptyEntries)) {
                int value;
                if(!int.TryParse(line.Trim(),out value) || value<10 || value>105)
                    throw new GpuTemperatureException("NVIDIA 温度数据无效。");
                gpu=Math.Max(gpu,value);
            }
            if(gpu<0)throw new GpuTemperatureException("NVIDIA 未返回温度数据。");
            return gpu;
        }
        public static int Read() {
            try {
                string smi=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System),"nvidia-smi.exe");
                var psi=new ProcessStartInfo(smi,"--query-gpu=temperature.gpu --format=csv,noheader,nounits") {
                    CreateNoWindow=true,UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true
                };
                using(var p=Process.Start(psi)) {
                    var output=new System.Text.StringBuilder();
                    p.OutputDataReceived+=(s,e)=> {if(e.Data!=null)output.AppendLine(e.Data);};
                    p.ErrorDataReceived+=(s,e)=>{};
                    p.BeginOutputReadLine();p.BeginErrorReadLine();
                    if(!p.WaitForExit(2500)) {try {p.Kill();}catch{}throw new GpuTemperatureException("GPU 温度读取超时。");}
                    p.WaitForExit(); // Drain asynchronous output after the process has exited.
                    if(p.ExitCode!=0)throw new GpuTemperatureException("NVIDIA 温度读取失败，退出码 "+p.ExitCode+"。");
                    return Parse(output.ToString());
                }
            }catch(GpuTemperatureException) {throw;}
            catch(Exception ex) {throw new GpuTemperatureException("GPU 温度读取不可用："+ex.Message,ex);}
        }
    }
    public sealed class GpuRecovery {
        public bool Waiting {get;private set;}
        public bool ResumeRequested {get;private set;}
        public int HealthyCount {get;private set;}
        public double NextAttempt {get;private set;}
        int failures;double firstHealthy,lastHealthy=double.NaN;
        public void Cancel() {Waiting=ResumeRequested=false;HealthyCount=failures=0;NextAttempt=0;lastHealthy=double.NaN;}
        public bool Due(double now) {return !Waiting || now>=NextAttempt;}
        public void Fail(double now,bool wantsControl) {
            Waiting=true;ResumeRequested|=wantsControl;HealthyCount=0;lastHealthy=double.NaN;
            failures=Math.Min(failures+1,4);NextAttempt=now+Math.Min(60,10*Math.Pow(2,failures-1));
        }
        public bool Healthy(double now) {
            if(!Waiting || !Due(now))return false;
            if(double.IsNaN(lastHealthy) || now-lastHealthy>6 || now<lastHealthy) {HealthyCount=0;firstHealthy=now;}
            HealthyCount++;lastHealthy=now;NextAttempt=now+2;
            return HealthyCount>=3 && now-firstHealthy>=4;
        }
    }
    public sealed class Backend : IDisposable {
        readonly object sync = new object();
        public const uint Fan1Id=0x04030001, Fan2Id=0x04030002, CpuId=0x05040000;
        readonly ManagementScope scope;
        ManagementObject device;
        public Backend() {
            scope=new ManagementScope(@"\\.\root\wmi");
            scope.Options.EnablePrivileges=true;
            scope.Options.Timeout=TimeSpan.FromSeconds(3);
            scope.Connect();
            using(var search=new ManagementObjectSearcher(scope,new ObjectQuery("SELECT * FROM LENOVO_OTHER_METHOD"))) {
                foreach(ManagementObject item in search.Get()) {device=item;break;}
            }
            if(device==null) throw new Exception("联想 WMI 接口不可用。");
        }
        public static void CheckMachine() {
            using(var search=new ManagementObjectSearcher("SELECT Manufacturer,Model FROM Win32_ComputerSystem")) {
                bool ok=false;
                foreach(ManagementObject item in search.Get())
                    ok=Convert.ToString(item["Manufacturer"]).Trim()=="LENOVO" && Convert.ToString(item["Model"]).StartsWith("21LE");
                if(!ok) throw new Exception("此版本仅针对实测的 LENOVO 21LE。");
            }
            using(var search=new ManagementObjectSearcher("SELECT SMBIOSBIOSVersion FROM Win32_BIOS")) {
                foreach(ManagementObject item in search.Get())
                    if(Convert.ToString(item["SMBIOSBIOSVersion"])!="NJCN66WW") throw new Exception("BIOS 版本与实测 NJCN66WW 不符，未启用控制。");
            }
        }
        public int Read(uint id) {
            lock(sync) {
            using(var input=device.GetMethodParameters("GetFeatureValue")) {
                input["IDs"]=id;
                using(var output=device.InvokeMethod("GetFeatureValue",input,new InvokeMethodOptions {Timeout=TimeSpan.FromSeconds(3)})) {
                    if(output==null || output["value"]==null) throw new Exception("WMI 未返回有效数据。");
                    return checked((int)Convert.ToUInt32(output["value"]));
                }
            }
        }
        }
        public void Write(uint id, int value) {
            lock(sync) {
            if(id!=Fan1Id && id!=Fan2Id) throw new Exception("未允许的写入接口。");
            if(value!=0 && (value%100!=0 || value<(id==Fan1Id?1500:1900) || value>(id==Fan1Id?4500:4800)))
                throw new Exception("目标 RPM 超出本机已确认范围。");
            using(var input=device.GetMethodParameters("SetFeatureValue")) {
                input["IDs"]=id;input["value"]=(uint)value;
                using(var output=device.InvokeMethod("SetFeatureValue",input,new InvokeMethodOptions {Timeout=TimeSpan.FromSeconds(3)})) {}
            }
        }
        }
        public void Auto() {
            // On NJCN66WW, either zero target sends the shared EC command 0x46/0x84.
            Exception failure=null;
            try {Write(Fan1Id,0);} catch(Exception ex) {failure=ex;}
            try {Write(Fan2Id,0);} catch(Exception ex) {if(failure!=null) throw new Exception("两个自动恢复命令均失败。",ex);return;}
            // One successful 0x84 command restores both fans on this exact firmware.
        }
        public Snapshot ReadSnapshot() {
            int cpu=Read(CpuId), fan1=Read(Fan1Id), fan2=Read(Fan2Id);
            int gpu=GpuTemperature.Read();
            if(cpu<10 || cpu>110 || gpu<10 || gpu>105 || fan1<0 || fan1>6500 || fan2<0 || fan2>6500)
                throw new Exception("温度或转速读数超出合理范围。");
            return new Snapshot {Cpu=cpu,Gpu=gpu,Fan1=fan1,Fan2=fan2,At=DateTime.Now};
        }
        public void Dispose() {lock(sync) {if(device!=null) {device.Dispose();device=null;}}}
    }
    public static class Files {
        public static readonly string Dir=AppDomain.CurrentDomain.BaseDirectory;
        public static readonly string Marker=Path.Combine(Dir,"curve-recovery.json");
        public static readonly string Config=Path.Combine(Dir,"curve-settings.json");
        public static readonly string LogPath=Path.Combine(Dir,"curve.log");
        public static void Log(string message) {
            try {
                if(File.Exists(LogPath) && new FileInfo(LogPath).Length>1024*1024) File.Copy(LogPath,LogPath+".previous",true);
                if(File.Exists(LogPath) && new FileInfo(LogPath).Length>1024*1024) File.WriteAllText(LogPath,"");
                File.AppendAllText(LogPath,DateTime.Now.ToString("o")+" "+message+Environment.NewLine);
            } catch {}
        }
        public static void Mark() {
            File.WriteAllText(Marker,"{\"Model\":\"21LE\",\"BIOS\":\"NJCN66WW\",\"ManualControlMayBeActive\":true}");
        }
        public static void Clear() {if(File.Exists(Marker)) File.Delete(Marker);}
    }
    public sealed class Guard : IDisposable {
        public readonly EventWaitHandle Pulse, Armed, Fault, Ready, Stop;
        public readonly Mutex Gate;
        public Process Child;
        readonly string name;
        public Guard(string eventBase) {
            name=eventBase;
            Pulse=new EventWaitHandle(false,EventResetMode.AutoReset,name+".pulse");
            Armed=new EventWaitHandle(false,EventResetMode.ManualReset,name+".armed");
            Fault=new EventWaitHandle(false,EventResetMode.ManualReset,name+".fault");
            Ready=new EventWaitHandle(false,EventResetMode.ManualReset,name+".ready");
            Stop=new EventWaitHandle(false,EventResetMode.ManualReset,name+".stop");
            Gate=new Mutex(false,@"Local\FanCurve21LE.Hardware");
        }
        public bool Lock(int milliseconds) {
            try {return Gate.WaitOne(milliseconds);} catch(AbandonedMutexException) {return true;}
        }
        public void Start() {
            Ready.Reset();Stop.Reset();
            string exe=Application.ExecutablePath;
            Child=Process.Start(new ProcessStartInfo(exe,"--watchdog "+Process.GetCurrentProcess().Id+" "+name) {
                UseShellExecute=false,CreateNoWindow=true
            });
            if(!Ready.WaitOne(5000) || Child.HasExited) throw new Exception("异常退出保护进程未能启动。");
        }
        public bool Alive {get {return Child!=null && !Child.HasExited;}}
        public static int WaitForActivity(bool armed,WaitHandle pulse,WaitHandle parentExited,WaitHandle stop,WaitHandle arm) {
            // Observe Arm itself: a pulse can be consumed just before the caller arms the watchdog.
            return WaitHandle.WaitAny(armed?new [] {pulse,parentExited,stop}:new [] {pulse,parentExited,stop,arm},armed?500:Timeout.Infinite);
        }
        public static int Watch(int parentId,string eventBase) {
            using(var guard=new Guard(eventBase)) {
                Process parent;
                try {parent=Process.GetProcessById(parentId);} catch {return 1;}
                var age=Stopwatch.StartNew();
                using(var parentExited=new EventWaitHandle(false,EventResetMode.ManualReset)) {
                parentExited.SafeWaitHandle=new Microsoft.Win32.SafeHandles.SafeWaitHandle(parent.Handle,false);
                guard.Ready.Set();
                while(true) {
                    bool alive;
                    try {alive=!parent.HasExited;} catch {alive=false;}
                    int signal=WaitForActivity(guard.Armed.WaitOne(0),guard.Pulse,parentExited,guard.Stop,guard.Armed);
                    if(signal==0 || signal==3)age.Restart();
                    if(signal==1 || signal==2)alive=false;
                    bool armed=guard.Armed.WaitOne(0);
                    if(!armed) {age.Restart();if(!alive) break;continue;}
                    if(alive && age.ElapsedMilliseconds<12000) continue;
                    guard.Fault.Set();
                    // Lock + fault prevents the UI from writing a manual target after this restore.
                    if(!guard.Lock(5000)) continue;
                    try {
                        using(var backend=new Backend())backend.Auto();
                        guard.Armed.Reset();Files.Clear();
                        Files.Log("WATCHDOG: automatic control restore commands sent.");
                    } catch(Exception ex) {Files.Log("WATCHDOG restore failed: "+ex.Message);Thread.Sleep(1000);}
                    finally {guard.Gate.ReleaseMutex();}
                    if(!alive && !guard.Armed.WaitOne(0)) break;
                }
                }
                parent.Dispose();
            }
            return 0;
        }
        public void Dispose() {if(Child!=null)Stop.Set();Pulse.Dispose();Armed.Dispose();Fault.Dispose();Ready.Dispose();Stop.Dispose();Gate.Dispose();if(Child!=null)Child.Dispose();}
    }
    public sealed class RpmDeviationMonitor {
        public int Count {get;private set;}
        public double Duration {get;private set;}
        double first,last=double.NaN;
        public void Reset() {Count=0;Duration=0;last=double.NaN;}
        public bool Observe(double now,double targetAge,int target1,int target2,int actual1,int actual2) {
            if(targetAge<12) {Reset();return false;}
            if(!double.IsNaN(last) && now==last)return false;
            if(!double.IsNaN(last) && (now<last || now-last>6))Reset();
            if(Math.Abs(actual1-target1)<=800 && Math.Abs(actual2-target2)<=800) {Reset();return false;}
            if(Count==0)first=now;
            last=now;Count++;Duration=now-first;
            return Count>=3 && Duration>=4;
        }
    }
    public sealed class RecoveryThrottle {
        double lastAttempt=double.NegativeInfinity;
        public bool TryRun(double now,Action restart) {
            if(now-lastAttempt<60)return false;
            restart();lastAttempt=now;return true;
        }
        public void Reset() {lastAttempt=double.NegativeInfinity;}
    }
    public sealed class StartupTransition {
        public enum Stage { Sampling, Ramping, Complete }
        public Stage Phase {get;private set;}
        public double Started {get;private set;}
        public double RampStarted {get;private set;}
        public int Count {get;private set;}
        public double CpuMean {get;private set;}
        public double GpuMean {get;private set;}
        public double Fan1Mean {get;private set;}
        public double Fan2Mean {get;private set;}
        public int[] EndTarget {get;private set;}
        double cpuSum,gpuSum,fan1Sum,fan2Sum,last=double.NaN;
        public void Begin(double now) {
            Phase=Stage.Sampling;Started=now;Count=0;cpuSum=gpuSum=fan1Sum=fan2Sum=0;
            CpuMean=GpuMean=Fan1Mean=Fan2Mean=0;EndTarget=null;last=double.NaN;
        }
        public void Cancel() {Phase=Stage.Complete;EndTarget=null;}
        static double Clamp(double value,int min,int max) {return Math.Max(min,Math.Min(max,value));}
        public int[] Update(double now,Snapshot sample,CurveConfig config) {
            if(Phase==Stage.Complete)return null;
            if(Phase==Stage.Sampling) {
                if(!double.IsNaN(last) && (now<last || now-last>6))Begin(now);
                if(now-Started<30 || Count==0) {
                    if(now!=last) {cpuSum+=sample.Cpu;gpuSum+=sample.Gpu;fan1Sum+=sample.Fan1;fan2Sum+=sample.Fan2;Count++;last=now;}
                    return null;
                }
                CpuMean=cpuSum/Count;GpuMean=gpuSum/Count;Fan1Mean=fan1Sum/Count;Fan2Mean=fan2Sum/Count;
                EndTarget=config.Target(CpuMean,GpuMean);RampStarted=now;Phase=Stage.Ramping;
            }
            double fraction=Math.Max(0,Math.Min(1,(now-RampStarted)/60));
            if(fraction>=1)Phase=Stage.Complete;
            double first=Clamp(Fan1Mean,1500,4500),second=Clamp(Fan2Mean,1900,4800);
            return new [] {(int)(Math.Ceiling((first+(EndTarget[0]-first)*fraction)/100)*100),
                          (int)(Math.Ceiling((second+(EndTarget[1]-second)*fraction)/100)*100)};
        }
    }
    public sealed class ThermalOverride {
        public bool Active {get;private set;}
        double lastWrite=double.NegativeInfinity;
        double? coolSince;
        public void Reset() {Active=false;lastWrite=double.NegativeInfinity;coolSince=null;}
        public bool Run(double now,int cpu,int gpu,Action takeover) {
            if(CurveConfig.IsFullSpeed(cpu,gpu)) {Active=true;coolSince=null;}
            else if(Active) {
                if(cpu<79 && gpu<71) {
                    if(!coolSince.HasValue)coolSince=now;
                    if(now-coolSince.Value>=15)Reset();
                } else coolSince=null;
            }
            if(!Active)return false;
            if(now-lastWrite>=6) {takeover();lastWrite=now;}
            return true;
        }
    }
    public sealed class DailyControlSchedule {
        public bool Paused {get;private set;}
        public static bool IsQuiet(DateTimeOffset time) {return time.ToOffset(TimeSpan.FromHours(8)).Hour<10;}
        public void Cancel() {Paused=false;}
        public bool Check(DateTimeOffset time,bool active,Action release,Action resume) {
            if(IsQuiet(time)) {
                if(active) {release();Paused=true;}
                return false;
            }
            if(Paused) {
                Paused=false;
                try {resume();}catch {Paused=true;throw;}
            }
            return true;
        }
    }
    public sealed class BatteryPause {
        public bool Paused {get;private set;}
        public bool ResumeRequested {get;private set;}
        public bool Released {get {return released;}}
        bool released;
        public void CancelResume() {ResumeRequested=false;}
        public bool Check(bool online,bool wantsControl,Action release,Action resume) {
            if(!online) {
                if(!Paused) {Paused=true;released=false;ResumeRequested=wantsControl;}
                if(!released) {release();released=true;}
                return false;
            }
            if(Paused) {
                if(!released) {release();released=true;}
                if(ResumeRequested)resume();
                Paused=false;ResumeRequested=false;
            }
            return true;
        }
    }
    public sealed class SamplingMinimum {
        readonly double[] lastWrite={double.NegativeInfinity,double.NegativeInfinity};
        public bool Applied {get;private set;}
        public void Reset() {lastWrite[0]=lastWrite[1]=double.NegativeInfinity;Applied=false;}
        public void Apply(double now,int fan1,int fan2,Action<int,int> write) {
            int[] actual={fan1,fan2};
            for(int i=0;i<2;i++) {
                if(actual[i]>=1500 || now-lastWrite[i]<6)continue;
                write(i,i==0?1500:1900);
                lastWrite[i]=now;Applied=true;
            }
        }
    }
    public sealed class Controller : IDisposable {
        public bool Active {get;private set;}
        readonly DailyControlSchedule schedule=new DailyControlSchedule();
        public bool ScheduledPaused {get {return schedule.Paused;}}
        public readonly Backend Hardware;
        public readonly Guard Guard;
        public CurveConfig Config;
        public int[] LastTarget;
        public readonly TemperatureAverage Average=new TemperatureAverage();
        public string Note="自动控制";
        double lastWrite=double.NegativeInfinity;
        readonly Stopwatch feedbackClock=Stopwatch.StartNew();
        readonly RpmDeviationMonitor deviation=new RpmDeviationMonitor();
        readonly RecoveryThrottle recovery=new RecoveryThrottle();
        readonly StartupTransition startup=new StartupTransition();
        readonly SamplingMinimum samplingMinimum=new SamplingMinimum();
        public bool SamplingFloorActive {get {return startup.Phase==StartupTransition.Stage.Sampling && samplingMinimum.Applied;}}
        readonly ThermalOverride heat=new ThermalOverride();
        double targetChangedAt,lastFeedbackLog=double.NegativeInfinity;
        double? downSince;
        public Controller(Backend backend,Guard guard) {Hardware=backend;Guard=guard;}
        public void Enable(CurveConfig config) {
            config.Validate();
            if(File.Exists(Files.Marker)) throw new Exception("发现未恢复记录，请先点击恢复自动。");
            if(!Guard.Alive) throw new Exception("保护进程未运行。");
            Config=config;recovery.Reset();heat.Reset();schedule.Cancel();
            if(DailyControlSchedule.IsQuiet(DateTimeOffset.UtcNow)) {
                schedule.Check(DateTimeOffset.UtcNow,true,Restore,()=>{});
                Note="定时暂停：UTC+8 00:00–10:00 由系统调速；10:00 后恢复曲线。";
                Files.Log("SCHEDULE_DEFER: enable requested during UTC+8 quiet hours.");return;
            }
            Guard.Fault.Reset();Guard.Pulse.Set();
            BeginStartup(feedbackClock.Elapsed.TotalSeconds);
        }
        void BeginStartup(double now) {
            Active=true;LastTarget=null;downSince=null;deviation.Reset();lastFeedbackLog=double.NegativeInfinity;
            Average.Clear();startup.Begin(now);samplingMinimum.Reset();
            Note="启动采样：前 30 秒记录温度与转速；低于 1500 RPM 时补至硬件最低转速。";
            Files.Log("STARTUP_SAMPLE_BEGIN: 30 seconds sampling with low-RPM floor (1500/1900), then 60 seconds linear ramp.");
        }
        public void Restore() {
            schedule.Cancel();
            if(!Guard.Lock(5000)) throw new Exception("硬件接口忙，恢复尚未完成。");
            try {
                startup.Cancel();heat.Reset();Active=false;Hardware.Auto();Guard.Armed.Reset();Files.Clear();
                LastTarget=null;deviation.Reset();Note="已发送恢复自动控制命令";Files.Log(Note);
            } finally {Guard.Gate.ReleaseMutex();}
        }
        public bool ApplySchedule() {
            bool allowed=schedule.Check(DateTimeOffset.UtcNow,Active || Guard.Armed.WaitOne(0),()=> {
                Restore();Files.Log("SCHEDULE_PAUSE: UTC+8 00:00-10:00; firmware automatic restored.");
            },()=> {
                Enable(Config);Files.Log("SCHEDULE_RESUME: UTC+8 quiet hours ended; fresh startup sampling.");
            });
            if(!allowed && ScheduledPaused)Note="定时暂停：UTC+8 00:00–10:00 由系统调速；10:00 后恢复曲线。";
            return allowed;
        }
        public void Tick(Snapshot snapshot) {
            if(!ApplySchedule())return;
            Average.Add(snapshot);
            if(!Active) return;
            if(!Guard.Alive || Guard.Fault.WaitOne(0)) {Active=false;throw new Exception("保护进程异常或已触发自动恢复。");}

            if(!Guard.Lock(1000)) throw new Exception("硬件接口忙。");
            try {
                if(!ApplySchedule() || !Active)return;
                if(Guard.Fault.WaitOne(0)) throw new Exception("已触发自动恢复。");
                Guard.Pulse.Set();
                double feedbackNow=feedbackClock.Elapsed.TotalSeconds;
                // Thermal takeover precedes sampling, ramping, deviation recovery and normal write throttling.
                if(heat.Run(feedbackNow,snapshot.Cpu,snapshot.Gpu,()=> {
                    Files.Mark();Guard.Pulse.Set();Guard.Armed.Set();
                    Hardware.Auto();
                    Hardware.Write(Backend.Fan1Id,4500);Hardware.Write(Backend.Fan2Id,4800);
                    LastTarget=new [] {4500,4800};lastWrite=feedbackNow;targetChangedAt=feedbackNow;
                    downSince=null;deviation.Reset();lastFeedbackLog=double.NegativeInfinity;
                    Files.Log("THERMAL_TAKEOVER Target=4500/4800 Actual="+snapshot.Fan1+"/"+snapshot.Fan2+" CPU="+snapshot.Cpu+" GPU="+snapshot.Gpu);
                })) {
                    startup.Cancel();
                    Note="过热保护：4500/4800 RPM，每 6 秒强制重新接管。";
                    Guard.Pulse.Set();return;
                }
                bool urgent=CurveConfig.IsFullSpeed(snapshot.Cpu,snapshot.Gpu);
                StartupTransition.Stage phaseBefore=startup.Phase;
                double samplingStart=startup.Started;
                int[] rampTarget=startup.Update(feedbackNow,snapshot,Config);
                if(startup.Started!=samplingStart) {
                    Average.Clear();Average.Add(snapshot);
                    Files.Log("STARTUP_SAMPLE_RESET: sampling gap; collecting a fresh 30-second window.");
                }
                if(startup.Phase==StartupTransition.Stage.Sampling) {
                    samplingMinimum.Apply(feedbackNow,snapshot.Fan1,snapshot.Fan2,(fan,rpm)=> {
                        Files.Mark();Guard.Pulse.Set();Guard.Armed.Set();
                        Hardware.Write(fan==0?Backend.Fan1Id:Backend.Fan2Id,rpm);
                        Files.Log("STARTUP_MINIMUM Fan="+(fan+1)+" Target="+rpm+" Actual="+(fan==0?snapshot.Fan1:snapshot.Fan2));
                    });
                    Note=String.Format("启动采样：{0:F0}/30 秒；记录实际温度与转速，低于 1500 时补至 1500/1900 RPM。",feedbackNow-startup.Started);
                    return;
                }
                bool transition=phaseBefore!=StartupTransition.Stage.Complete;
                bool transitionEnded=transition && startup.Phase==StartupTransition.Stage.Complete;
                if(phaseBefore==StartupTransition.Stage.Sampling) {
                    Files.Log(String.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "STARTUP_BASELINE Samples={0} AverageCPU={1:F2} AverageGPU={2:F2} AverageFan1={3:F2} AverageFan2={4:F2} EndTarget={5}/{6}",
                        startup.Count,startup.CpuMean,startup.GpuMean,startup.Fan1Mean,startup.Fan2Mean,startup.EndTarget[0],startup.EndTarget[1]));
                    Files.Mark();Guard.Pulse.Set();Guard.Armed.Set();
                }
                int[] target=transition?rampTarget:Average.Target(Config,snapshot);
                if(urgent) {
                    target=new [] {4500,4800};
                    if(transition) {startup.Cancel();transitionEnded=true;Files.Log("STARTUP_RAMP_EMERGENCY: immediate maximum target.");}
                }
                if(transitionEnded) {
                    deviation.Reset();targetChangedAt=feedbackNow;
                    Files.Log("STARTUP_RAMP_END: returning to 3-minute average control.");
                }
                if(LastTarget!=null && !transition) {
                    int previousCount=deviation.Count;
                    bool sustained=deviation.Observe(feedbackNow,feedbackNow-targetChangedAt,
                        LastTarget[0],LastTarget[1],snapshot.Fan1,snapshot.Fan2);
                    string detail=String.Format(System.Globalization.CultureInfo.InvariantCulture,
                        "Target={0}/{1} Actual={2}/{3} Delta={4}/{5} Count={6} DeviationSeconds={7:F1} TargetAgeSeconds={8:F1} CPU={9} GPU={10} AverageCPU={11:F1} AverageGPU={12:F1}",
                        LastTarget[0],LastTarget[1],snapshot.Fan1,snapshot.Fan2,
                        snapshot.Fan1-LastTarget[0],snapshot.Fan2-LastTarget[1],deviation.Count,deviation.Duration,
                        feedbackNow-targetChangedAt,snapshot.Cpu,snapshot.Gpu,Average.Cpu,Average.Gpu);
                    if(deviation.Count>0 || previousCount>0 || feedbackNow-lastFeedbackLog>=30) {
                        Files.Log((deviation.Count>0?"RPM_DEVIATION ":previousCount>0?"RPM_DEVIATION_RESET ":"RPM_FEEDBACK ")+detail);
                        lastFeedbackLog=feedbackNow;
                    }
                    if(sustained && !urgent && recovery.TryRun(feedbackNow,()=> {
                        Files.Log("RPM_REACQUIRE_BEGIN "+detail);
                        Hardware.Auto();Guard.Armed.Reset();Files.Clear();
                        BeginStartup(feedbackNow);Average.Add(snapshot);
                        startup.Update(feedbackNow,snapshot,Config);
                    }))return;


                }
                if(!transition && LastTarget!=null && target[0]<=LastTarget[0] && target[1]<=LastTarget[1] &&
                   (target[0]<LastTarget[0] || target[1]<LastTarget[1])) {
                    if(!downSince.HasValue) downSince=feedbackNow;
                    if(feedbackNow-downSince.Value<15) target=(int[])LastTarget.Clone();
                } else downSince=null;
                bool emergency=CurveConfig.IsFullSpeed(snapshot.Cpu,snapshot.Gpu);
                bool different=LastTarget==null || target[0]!=LastTarget[0] || target[1]!=LastTarget[1];
                bool due=LastTarget==null || feedbackNow-lastWrite>=6 || emergency || transitionEnded;
                if(different && due) {
                    Hardware.Write(Backend.Fan1Id,target[0]);
                    Hardware.Write(Backend.Fan2Id,target[1]);
                    LastTarget=target;lastWrite=feedbackNow;targetChangedAt=feedbackNow;downSince=null;deviation.Reset();lastFeedbackLog=double.NegativeInfinity;
                    Files.Log("Target="+target[0]+"/"+target[1]+" CPU="+snapshot.Cpu+" GPU="+snapshot.Gpu+" AverageCPU="+Average.Cpu.ToString("F1")+" AverageGPU="+Average.Gpu.ToString("F1"));
                }
                Note=emergency?"高温保护：最高转速":startup.Phase==StartupTransition.Stage.Ramping?String.Format("启动过渡：{0:F0}/60 秒；从记录均速线性过渡至 {1}/{2} RPM。",feedbackNow-startup.RampStarted,startup.EndTarget[0],startup.EndTarget[1]):deviation.Count>0?"曲线控制中（转速偏差，等待后台重新接管）":"3 分钟均温控制中（降速延迟 15 秒）";
                Guard.Pulse.Set();
            } finally {Guard.Gate.ReleaseMutex();}
        }
        public void Dispose() {if(Active || Guard.Armed.WaitOne(0)) Restore();}
    }
    public static class LoginStartup {
        public const string Owner="ThinkBookFanCurve21LE";
        public static string TaskName {get {return Owner+"-"+WindowsIdentity.GetCurrent().User.Value;}}
        static dynamic Root() {dynamic service=Activator.CreateInstance(Type.GetTypeFromProgID("Schedule.Service"));service.Connect();return service.GetFolder("\\");}
        static string Escape(string value) {return System.Security.SecurityElement.Escape(value);}
        public static string Xml(string executable,bool enabled,string arguments) {
            string sid=Escape(WindowsIdentity.GetCurrent().User.Value);
            return "<?xml version=\"1.0\" encoding=\"UTF-16\"?>" +
                "<Task version=\"1.3\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">" +
                "<RegistrationInfo><Description>Fan Curve: apply saved curve at user logon</Description><Source>"+Owner+"</Source></RegistrationInfo>" +
                "<Triggers><LogonTrigger><Enabled>true</Enabled><Delay>PT30S</Delay><UserId>"+sid+"</UserId></LogonTrigger></Triggers>" +
                "<Principals><Principal id=\"User\"><UserId>"+sid+"</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>" +
                "<Settings><MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy><DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>" +
                "<StopIfGoingOnBatteries>false</StopIfGoingOnBatteries><AllowHardTerminate>false</AllowHardTerminate><StartWhenAvailable>true</StartWhenAvailable>" +
                "<RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable><Enabled>"+(enabled?"true":"false")+"</Enabled>" +
                "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit></Settings>" +
                "<Actions Context=\"User\"><Exec><Command>"+Escape(Path.GetFullPath(executable))+"</Command><Arguments>"+Escape(arguments)+"</Arguments>" +
                "<WorkingDirectory>"+Escape(Path.GetDirectoryName(Path.GetFullPath(executable)))+"</WorkingDirectory></Exec></Actions></Task>";
        }
        static string ReadXml(string name) {
            try {return (string)Root().GetTask(name).Xml;}
            catch(Exception ex) {if((uint)ex.HResult==0x80070002 || (uint)ex.HResult==0x80070003)return null;throw;}
        }
        static string Value(string xml,string xpath) {
            var doc=new System.Xml.XmlDocument();doc.LoadXml(xml);
            var ns=new System.Xml.XmlNamespaceManager(doc.NameTable);ns.AddNamespace("t","http://schemas.microsoft.com/windows/2004/02/mit/task");
            var node=doc.SelectSingleNode(xpath,ns);return node==null?null:node.InnerText;
        }
        static void CheckOwner(string xml) {
            if(xml!=null && Value(xml,"/t:Task/t:RegistrationInfo/t:Source")!=Owner) throw new Exception("同名计划任务不是本程序创建的，未修改。");
        }
        public static void Register(string name,string executable,bool enabled,string arguments) {
            CheckOwner(ReadXml(name));
            Root().RegisterTask(name,Xml(executable,enabled,arguments),6,null,null,3,null);
            string actual=ReadXml(name);
            if(Value(actual,"/t:Task/t:Actions/t:Exec/t:Command")!=Path.GetFullPath(executable) ||
               Value(actual,"/t:Task/t:Actions/t:Exec/t:Arguments")!=arguments) throw new Exception("启动项路径验证失败。");
        }
        public static void Remove(string name) {
            string xml=ReadXml(name);if(xml==null)return;CheckOwner(xml);Root().DeleteTask(name,0);
            if(ReadXml(name)!=null)throw new Exception("启动项删除验证失败。");
        }
        public static string Status() {
            string xml=ReadXml(TaskName);if(xml==null)return "登录启动：未注册";
            CheckOwner(xml);
            string target=Value(xml,"/t:Task/t:Actions/t:Exec/t:Command");
            bool here=String.Equals(target,Application.ExecutablePath,StringComparison.OrdinalIgnoreCase);
            bool enabled=Value(xml,"/t:Task/t:Settings/t:Enabled")!="false";
            return "登录启动："+(enabled?"已注册":"已禁用")+"；"+(here?"当前目录":"路径已变化，请重新注册")+"（登录后约 30 秒接管）";
        }
        public static bool SelfTest() {
            string name=TaskName+"-Test-"+Guid.NewGuid().ToString("N");
            string moved=Path.Combine(Path.GetTempPath(),"Fan Curve 移动测试 "+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(moved);
            string executable=Path.Combine(moved,"FanCurve.exe");
            try {
                File.Copy(Application.ExecutablePath,executable);
                Register(name,Application.ExecutablePath,false,"--autostart");
                string first=ReadXml(name);
                if(Value(first,"/t:Task/t:Principals/t:Principal/t:RunLevel")!="HighestAvailable" ||
                   Value(first,"/t:Task/t:Principals/t:Principal/t:LogonType")!="InteractiveToken" ||
                   Value(first,"/t:Task/t:Settings/t:ExecutionTimeLimit")!="PT0S") throw new Exception("Task settings mismatch.");
                Register(name,executable,false,"--autostart");
                string next=ReadXml(name);
                if(Value(next,"/t:Task/t:Actions/t:Exec/t:WorkingDirectory")!=moved)throw new Exception("Relocation failed.");
                Remove(name);
                File.WriteAllText(Path.Combine(Files.Dir,"startup-self-test.txt"),"PASS: real task registration, highest interactive privileges, disabled test task, relocation with spaces/Chinese path, deletion. Production login task unchanged.");
                return true;
            } finally {
                Remove(name);
                // Only remove exact files created by this test; no recursive deletion.
                if(File.Exists(executable))File.Delete(executable);
                if(Directory.Exists(moved))Directory.Delete(moved,false);
            }
        }
    }

    // No MainForm is assigned: Application.Run must not show a window at logon.
    public sealed class TrayApplicationContext : ApplicationContext {
        readonly MainForm window;
        public TrayApplicationContext(MainForm form) {
            window=form;window.FormClosed+=WindowClosed;
            window.StartBackground();
        }
        void WindowClosed(object sender,FormClosedEventArgs e) {ExitThread();}
        protected override void Dispose(bool disposing) {
            if(disposing)window.FormClosed-=WindowClosed;
            base.Dispose(disposing);
        }
    }
    public sealed class MainForm : Form {
        readonly Label readings=new Label(), status=new Label(), targetText=new Label();
        readonly DataGridView grid=new DataGridView();
        readonly NumericUpDown offset=new NumericUpDown();
        readonly Button start=new Button(), stop=new Button(), save=new Button(), registerStartup=new Button(), removeStartup=new Button();
        readonly Label startupStatus=new Label();
        bool pendingAuto,savedConfigValid; readonly bool loginLaunch; DateTime startupDeadline;
        readonly Chart chart=new Chart();
        readonly System.Windows.Forms.Timer timer=new System.Windows.Forms.Timer();
        Controller controller; Guard guard;
        bool busy,closing,exitRequested;
        bool suspended,resumeAfterSleep;
        readonly GpuRecovery gpuRecovery=new GpuRecovery();
        readonly Stopwatch sensorClock=Stopwatch.StartNew();
        internal Func<PowerLineStatus> ReadPowerStatus=()=>SystemInformation.PowerStatus.PowerLineStatus;
        // Keep callbacks alive when showing/hiding recreates the form handle.
        readonly Control dispatcher=new Control();
        readonly BatteryPause battery=new BatteryPause();
        int powerGeneration;
        bool background,sessionStarted;
        Action initializeSession;
        readonly NotifyIcon tray=new NotifyIcon();
        readonly ContextMenuStrip trayMenu=new ContextMenuStrip();
        protected override void SetVisibleCore(bool value) {base.SetVisibleCore(value && !background);}
        public void StartBackground() {
            HideWindow();
            var handle=Handle;
            if(!sessionStarted) {sessionStarted=true;initializeSession();}
        }
        void OpenWindow() {background=false;ShowInTaskbar=true;Show();WindowState=FormWindowState.Normal;Activate();}
        void HideWindow() {background=true;ShowInTaskbar=false;Hide();}
        void RequestExit() {exitRequested=true;if(!busy)Close();}
        Microsoft.Win32.PowerModeChangedEventHandler powerHandler;
        public MainForm(bool smoke, bool autostart) {
            var dispatchHandle=dispatcher.Handle;
            loginLaunch=autostart;
            background=autostart;ShowInTaskbar=!autostart;
            using(var iconStream=typeof(MainForm).Assembly.GetManifestResourceStream("FanCurve.Icon")) {
                Icon=new Icon(iconStream);
            }
            tray.Icon=Icon;tray.Text="Fan Curve 风扇控制";
            trayMenu.Items.Add("打开界面",null,(s,e)=>OpenWindow());
            trayMenu.Items.Add("隐藏到托盘",null,(s,e)=>HideWindow());
            trayMenu.Items.Add("恢复自动并退出",null,(s,e)=>RequestExit());
            tray.ContextMenuStrip=trayMenu;tray.DoubleClick+=(s,e)=>OpenWindow();tray.Visible=true;

            Font=new Font("Microsoft YaHei UI",10);
            Text="Fan Curve · ThinkBook 16+ 2024";ClientSize=new Size(980,740);
            StartPosition=FormStartPosition.CenterScreen;MinimumSize=new Size(740,570);
            BackColor=Color.FromArgb(247,249,251);DoubleBuffered=true;
            var root=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=9,Padding=new Padding(18),AutoScroll=true};
            for(int i=0;i<9;i++)root.RowStyles.Add(new RowStyle(i==4?SizeType.Percent:SizeType.AutoSize,i==4?100:0));
            root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            Controls.Add(root);
            var heading=new Label {Text="温度 → 风扇转速",AutoSize=true,Font=new Font(Font.FontFamily,17,FontStyle.Bold),Margin=new Padding(0,0,0,5)};
            root.Controls.Add(heading,0,0);
            root.Controls.Add(new Label {Text="21LE / NJCN66WW · 联想 WMI 直接调速",AutoSize=true,Margin=new Padding(0,0,0,8)},0,1);
            readings.AutoSize=true;readings.Font=new Font(Font.FontFamily,11,FontStyle.Bold);readings.Margin=new Padding(0,0,0,8);root.Controls.Add(readings,0,2);
            targetText.AutoSize=true;targetText.Margin=new Padding(0,0,0,10);root.Controls.Add(targetText,0,3);
            var middle=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=new Padding(0)};
            middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,59));middle.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,41));
            root.Controls.Add(middle,0,4);
            chart.Dock=DockStyle.Fill;chart.BackColor=BackColor;chart.Margin=new Padding(0,0,12,0);
            var area=new ChartArea("curve");
            area.AxisX.Minimum=30;area.AxisX.Maximum=110;area.AxisX.Interval=10;area.AxisX.Title="用于曲线的温度 (°C)";
            area.AxisY.Minimum=1000;area.AxisY.Maximum=5000;area.AxisY.Interval=1000;area.AxisY.Title="目标 RPM";
            area.BackColor=Color.White;
            area.AxisX.MajorGrid.LineColor=area.AxisY.MajorGrid.LineColor=Color.FromArgb(225,231,235);
            chart.ChartAreas.Add(area);chart.Legends.Add(new Legend {Docking=Docking.Top});
            foreach(string name in new [] {"风扇 1","风扇 2"}) {
                var series=new Series(name) {ChartType=SeriesChartType.Line,BorderWidth=3,MarkerStyle=MarkerStyle.Circle,MarkerSize=7};
                series.Color=name=="风扇 1"?Color.Teal:Color.MediumPurple;chart.Series.Add(series);
            }
            middle.Controls.Add(chart,0,0);
            var right=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=2,Margin=new Padding(0)};
            right.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
            right.RowStyles.Add(new RowStyle(SizeType.Percent,100));right.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            middle.Controls.Add(right,1,0);
            grid.Dock=DockStyle.Fill;grid.Margin=new Padding(0,0,0,8);
            grid.AllowUserToAddRows=false;grid.AllowUserToDeleteRows=false;grid.RowHeadersVisible=false;
            grid.AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill;grid.BackgroundColor=Color.White;
            grid.ColumnHeadersHeightSizeMode=DataGridViewColumnHeadersHeightSizeMode.AutoSize;
            grid.AutoSizeRowsMode=DataGridViewAutoSizeRowsMode.AllCells;
            grid.Columns.Add("temp","温度 °C");grid.Columns[0].ReadOnly=true;grid.Columns.Add("fan1","风扇1 RPM");grid.Columns.Add("fan2","风扇2 RPM");
            right.Controls.Add(grid,0,0);
            var editRow=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill,WrapContents=true,Margin=new Padding(0)};
            editRow.Controls.Add(new Label {Text="温度偏移",AutoSize=true,Margin=new Padding(0,8,8,0)});
            offset.Width=65;offset.Minimum=0;offset.Maximum=15;offset.Margin=new Padding(0,3,4,0);editRow.Controls.Add(offset);
            editRow.Controls.Add(new Label {Text="°C",AutoSize=true,Margin=new Padding(0,8,8,0)});
            StyleButton(save,"保存曲线",95);editRow.Controls.Add(save);right.Controls.Add(editRow,0,1);
            var actions=new TableLayoutPanel {AutoSize=true,Dock=DockStyle.Fill,ColumnCount=2,RowCount=1,Margin=new Padding(0,10,0,8)};
            actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,59));actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,41));
            root.Controls.Add(actions,0,5);
            var notes=new Label {Text="曲线温度 = max(CPU均温, GPU均温) + 偏移\n3 分钟均温；CPU82°C / GPU74°C 循环全速。\nUTC+8 00:00–10:00 暂停接管，由系统调速。",AutoSize=true,Dock=DockStyle.Fill,Margin=new Padding(0,0,10,0)};
            actions.Controls.Add(notes,0,0);
            var buttons=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill,WrapContents=true,Margin=new Padding(0)};
            StyleButton(start,"启用曲线",130);StyleButton(stop,"恢复自动",130);
            buttons.Controls.Add(start);buttons.Controls.Add(stop);actions.Controls.Add(buttons,1,0);
            var startupRow=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill,WrapContents=true,Margin=new Padding(0,4,0,4)};
            StyleButton(registerStartup,"注册/更新登录启动",175);StyleButton(removeStartup,"删除登录启动",145);
            startupRow.Controls.Add(registerStartup);startupRow.Controls.Add(removeStartup);root.Controls.Add(startupRow,0,6);
            startupStatus.AutoSize=true;startupStatus.Dock=DockStyle.Fill;startupStatus.Margin=new Padding(0,3,0,6);root.Controls.Add(startupStatus,0,7);
            status.AutoSize=true;status.Dock=DockStyle.Fill;status.MinimumSize=new Size(0,34);status.Margin=new Padding(0,4,0,0);root.Controls.Add(status,0,8);
            registerStartup.Click+=(s,e)=>StartupAction(()=> {
                var c=ReadConfig();c.Validate();SaveConfig(c);
                LoginStartup.Register(LoginStartup.TaskName,Application.ExecutablePath,true,"--autostart");
                status.Text="已保存曲线并注册登录启动；下次登录约 30 秒后自动接管。";
            });
            removeStartup.Click+=(s,e)=>StartupAction(()=> {
                LoginStartup.Remove(LoginStartup.TaskName);
                pendingAuto=false;
                status.Text="登录启动已删除；当前风扇状态不变。";
            });
            start.Click+=(s,e)=>RunAction(()=> {
                if(!CheckPower())return;
                if(busy) throw new Exception("正在读取温度，请稍后再启用。");
                CurveConfig c=ReadConfig();c.Validate();
                SaveConfig(c);gpuRecovery.Cancel();pendingAuto=true;powerGeneration++;
                startupDeadline=DateTime.UtcNow.AddMinutes(2);UpdateControls();Poll();
            });
            stop.Click+=(s,e)=>RunAction(()=> {pendingAuto=false;resumeAfterSleep=false;gpuRecovery.Cancel();battery.CancelResume();if(controller!=null && (!battery.Paused || !battery.Released))controller.Restore();UpdateControls();status.Text=battery.Paused?"电池供电：系统调速；已取消接电后自动接管。":controller==null?"已取消自动接管。":controller.Note;});
            save.Click+=(s,e)=>RunAction(()=> {var c=ReadConfig();c.Validate();SaveConfig(c);Plot(c);status.Text="曲线已保存；点击启用曲线后才会控制风扇。";});
            grid.CellEndEdit+=(s,e)=> {try {var c=ReadConfig();c.Validate();Plot(c);} catch(Exception ex) {status.Text=ex.Message;}};
            LoadConfig();RefreshStartupStatus();
            initializeSession=()=> {
                Rectangle available=Screen.FromControl(this).WorkingArea;
                Size=new Size(Math.Min(Width,available.Width-24),Math.Min(Height,available.Height-24));
                if(smoke) {
                    readings.Text="CPU 54°C     GPU 47°C     风扇 1：1500 RPM     风扇 2：1900 RPM（布局测试示例）";
                    targetText.Text="目标：固件自动";status.Text="界面布局测试；没有调用硬件。";
                    PerformLayout();
                    var all=new List<Control>();CollectControls(this,all);
                    foreach(Button button in all.OfType<Button>()) {
                        if(String.IsNullOrWhiteSpace(button.Text))throw new Exception("Blank button label.");
                        Rectangle rect=button.RectangleToScreen(button.ClientRectangle);
                        foreach(Label label in all.OfType<Label>()) {
                            if(label.Visible && rect.IntersectsWith(label.RectangleToScreen(label.ClientRectangle)))
                                { using(var debugImage=new Bitmap(Width,Height)){DrawToBitmap(debugImage,new Rectangle(0,0,Width,Height));debugImage.Save(Path.Combine(Files.Dir,"curve-ui.png"));} throw new Exception("Label overlaps button: "+button.Text+" "+rect+" label="+label.Text+" "+label.RectangleToScreen(label.ClientRectangle)); }
                        }
                    }
                    File.WriteAllText(Path.Combine(Files.Dir,"curve-layout-test.txt"),"PASS: all buttons have text; no Label overlaps any button at current display DPI.");
                    using(var bitmap=new Bitmap(Width,Height)) {DrawToBitmap(bitmap,new Rectangle(0,0,Width,Height));bitmap.Save(Path.Combine(Files.Dir,"curve-ui.png"));}
                    var closeTimer=new System.Windows.Forms.Timer {Interval=300};closeTimer.Tick+=(a,b)=>{closeTimer.Stop();RequestExit();closeTimer.Dispose();};closeTimer.Start();return;
                }
                Rectangle work=Screen.FromControl(this).WorkingArea;
                Size=new Size(Math.Min(Width,work.Width-24),Math.Min(Height,work.Height-24));
                Location=new Point(work.Left+(work.Width-Width)/2,work.Top+(work.Height-Height)/2);
                pendingAuto=autostart && savedConfigValid;
                startupDeadline=DateTime.UtcNow.AddMinutes(2);
                powerHandler=(sender,powerEvent)=> {
                    try {
                        Action handle=()=>HandlePowerChange(powerEvent.Mode);
                        if(powerEvent.Mode==Microsoft.Win32.PowerModes.Suspend)dispatcher.Invoke(handle);
                        else dispatcher.BeginInvoke(handle);
                    }catch(InvalidOperationException){}
                };
                Microsoft.Win32.SystemEvents.PowerModeChanged+=powerHandler;
                timer.Interval=2000;timer.Tick+=(a,b)=>Poll();timer.Start();Poll();
                if(Environment.GetCommandLineArgs().Contains("--startup-probe")) {
                    var probeTimer=new System.Windows.Forms.Timer {Interval=15000};
                    probeTimer.Tick+=(sender,eventArgs)=> {
                        if(busy)return;
                        probeTimer.Stop();
                        Snapshot sample=controller==null || battery.Paused?null:controller.Hardware.ReadSnapshot();
                        File.WriteAllText(Path.Combine(Files.Dir,"startup-probe.json"),
                            new JavaScriptSerializer().Serialize(new {Active=controller!=null && controller.Active,
                            Offset=controller!=null && controller.Config!=null?controller.Config.Offset:-1,
                            Target=controller==null?null:controller.LastTarget,Snapshot=sample,Status=status.Text}));
                        RequestExit();probeTimer.Dispose();
                    };
                    probeTimer.Start();
                }

            };
            Shown+=(s,e)=> {if(!sessionStarted) {sessionStarted=true;initializeSession();}};
            FormClosing+=(s,e)=> {
                if(e.CloseReason==CloseReason.UserClosing && !exitRequested) {e.Cancel=true;HideWindow();return;}
                bool shutdown=e.CloseReason==CloseReason.WindowsShutDown;
                if(busy && !shutdown) {exitRequested=true;e.Cancel=true;status.Text="正在等待读取结束，随后退出。";return;}
                closing=true;timer.Stop();
                try {
                    if(controller!=null && (controller.Active || guard.Armed.WaitOne(0))) controller.Restore();
                } catch(Exception ex) {
                    Files.Log("EXIT_RESTORE_FAILED: "+ex.Message);
                    if(!shutdown) {e.Cancel=true;closing=false;exitRequested=false;OpenWindow();status.Text="自动恢复失败，窗口保持打开："+ex.Message;timer.Start();}
                }
            };
            FormClosed+=(s,e)=> {if(powerHandler!=null) Microsoft.Win32.SystemEvents.PowerModeChanged-=powerHandler;timer.Dispose();tray.Dispose();trayMenu.Dispose();dispatcher.Dispose();if(guard!=null)guard.Dispose();if(controller!=null && !busy)controller.Hardware.Dispose();};
            AutoScaleDimensions=new SizeF(96,96);AutoScaleMode=AutoScaleMode.Dpi;
        }
        static void CollectControls(Control parent,List<Control> result) {foreach(Control child in parent.Controls) {result.Add(child);CollectControls(child,result);}}
        static void StyleButton(Button button,string text,int width) {
            button.Text=text;button.AutoSize=true;button.MinimumSize=new Size(width,38);
            button.Padding=new Padding(8,3,8,3);button.Margin=new Padding(0,0,8,3);
            button.UseVisualStyleBackColor=true;
        }
        void StartupAction(Action action) {
            try {action();} catch(Exception ex) {status.Text="启动项操作失败："+ex.Message;Files.Log(status.Text);}
            RefreshStartupStatus();
        }
        void RefreshStartupStatus() {
            try {startupStatus.Text=LoginStartup.Status();}
            catch(Exception ex) {startupStatus.Text="登录启动状态读取失败："+ex.Message;}
        }
        void TryInitialize() {
            if(controller!=null)return;
            Backend backend=null;Guard newGuard=null;
            try {
                Backend.CheckMachine();
                backend=new Backend();
                newGuard=new Guard(@"Local\FanCurve21LE."+Guid.NewGuid().ToString("N"));newGuard.Start();
                controller=new Controller(backend,newGuard);guard=newGuard;
                status.Text=pendingAuto?"登录启动：等待有效温度后接管。":
                    (loginLaunch && !savedConfigValid?"自动接管已取消：没有有效的已保存曲线，请保存后重新注册。":
                    (File.Exists(Files.Marker)?"检测到未恢复记录，请先点击恢复自动。":"当前为监控状态；点击启用曲线才会接管。"));
                UpdateControls();
            } catch(Exception ex) {
                if(controller==null) {if(newGuard!=null)newGuard.Dispose();if(backend!=null)backend.Dispose();}
                if(pendingAuto && DateTime.UtcNow<startupDeadline)status.Text="登录启动：等待驱动就绪，最多重试 2 分钟。"+ex.Message;
                else {pendingAuto=false;status.Text="初始化失败："+ex.Message;}
            }
        }

        Label AddLabel(string text,int x,int y,int w,int h) {var l=new Label {Text=text};l.SetBounds(x,y,w,h);Controls.Add(l);return l;}
        void RunAction(Action action) {
            try {action();}
            catch(Exception ex) {
                string message=ex.Message;
                if(controller!=null && (controller.Active || guard.Armed.WaitOne(0))) {
                    try {controller.Restore();message+="；已发送恢复自动控制命令。";}
                    catch(Exception restore) {message+="；恢复失败："+restore.Message+"，保护进程将重试。";}
                }
                status.Text=message;Files.Log(message);UpdateControls();
            }
        }
        void HandlePowerChange(Microsoft.Win32.PowerModes mode) {
            if(closing || exitRequested)return;
            if(mode==Microsoft.Win32.PowerModes.Suspend) {
                if(suspended)return;
                suspended=true;powerGeneration++;timer.Stop();
                resumeAfterSleep=pendingAuto || gpuRecovery.ResumeRequested || battery.ResumeRequested || (controller!=null && (controller.Active || controller.ScheduledPaused));
                pendingAuto=false;gpuRecovery.Cancel();
                RunAction(()=> {
                    if(controller!=null && (controller.Active || controller.ScheduledPaused || guard.Armed.WaitOne(0)))controller.Restore();
                    Files.Log("SUSPEND: automatic control; resume intent="+resumeAfterSleep);
                });
                return;
            }
            if(mode==Microsoft.Win32.PowerModes.Resume && suspended) {
                suspended=false;powerGeneration++;
                pendingAuto=resumeAfterSleep;resumeAfterSleep=false;
                startupDeadline=DateTime.UtcNow.AddMinutes(2);
                Files.Log("WAKE: discard pre-sleep readings; reevaluate power and schedule.");
                timer.Start();
            }
            Poll();
        }
        bool CheckPower() {
            bool online=ReadPowerStatus()==PowerLineStatus.Online;
            bool wasPaused=battery.Paused;
            timer.Interval=online?2000:3600000;
            bool allowed=false,success=false;
            RunAction(()=> {
                allowed=battery.Check(online,pendingAuto || gpuRecovery.ResumeRequested || (controller!=null && (controller.Active || controller.ScheduledPaused)),()=> {
                    pendingAuto=false;gpuRecovery.Cancel();
                    if(controller!=null) {
                        if(controller.Active || controller.ScheduledPaused || guard.Armed.WaitOne(0) || File.Exists(Files.Marker))controller.Restore();
                        controller.Average.Clear();
                    } else if(File.Exists(Files.Marker)) {
                        TryInitialize();
                        if(controller==null)throw new Exception("电池模式自动恢复等待硬件接口就绪。");
                        controller.Restore();
                    }
                    Files.Log("BATTERY_PAUSE: firmware automatic; sensor polling stopped; hourly power fallback.");
                },()=> {
                    pendingAuto=true;startupDeadline=DateTime.UtcNow.AddMinutes(2);
                    Files.Log("AC_RESUME: saved control intent restored; fresh startup sampling pending.");
                });
                success=true;
            });
            if(wasPaused!=battery.Paused)powerGeneration++;
            if(battery.Paused) {
                readings.Text="电池供电／外接电源未确认：已停止温度与转速采样";
                targetText.Text=battery.Released?"目标：系统自动控制":"目标：等待确认恢复系统控制";
                if(success)status.Text=battery.ResumeRequested?"节电待机：接通电源后恢复曲线；电源事件检测，每小时兜底检查。":"节电待机：接通电源后恢复监控；电源事件检测，每小时兜底检查。";
            }
            UpdateControls();return allowed;
        }
        void Poll() {
            if(closing || suspended)return;
            if(exitRequested) {if(!busy)Close();return;}
            if(!CheckPower())return;
            // Check the schedule before polling, including while a previous read is pending.
            if(controller!=null)RunAction(()=> {controller.ApplySchedule();UpdateControls();if(controller.ScheduledPaused)status.Text=controller.Note;});
            if(busy)return;
            if(!gpuRecovery.Due(sensorClock.Elapsed.TotalSeconds))return;
            if(controller==null) {TryInitialize();if(controller==null)return;}
            busy=true;
            int sampleGeneration=powerGeneration;
            // Hardware work stays off the UI thread. Only one poll exists at a time.
            ThreadPool.QueueUserWorkItem(_=> {
                Snapshot snapshot=null;Exception failure=null;
                try {snapshot=controller.Hardware.ReadSnapshot();}
                catch(Exception ex) {failure=ex;}
                try {
                    dispatcher.BeginInvoke((Action)(()=> {
                        busy=false;if(closing)return;
                        if(exitRequested) {Close();return;}
                        if(suspended)return;
                        if(!CheckPower() || sampleGeneration!=powerGeneration)return;
                        if(failure is GpuTemperatureException || (failure!=null && gpuRecovery.Waiting)) {
                            HandleGpuFailure(failure);return;
                        }
                        if(gpuRecovery.Waiting) {
                            bool resume=gpuRecovery.ResumeRequested;
                            if(!gpuRecovery.Healthy(sensorClock.Elapsed.TotalSeconds)) {
                                readings.Text=String.Format("CPU {0}°C    GPU {1}°C（恢复确认 {2}/3）",snapshot.Cpu,snapshot.Gpu,gpuRecovery.HealthyCount);
                                status.Text="温度读取已恢复，等待连续 3 次有效读数后"+(resume?"重新接管。":"恢复监控。");
                                return;
                            }
                            gpuRecovery.Cancel();
                            pendingAuto=resume;startupDeadline=DateTime.UtcNow.AddMinutes(2);
                            Files.Log("GPU_RECOVERED: three valid snapshots; resume="+resume);
                        }
                        if(pendingAuto) {
                            if(DateTime.UtcNow>startupDeadline) {
                                pendingAuto=false;status.Text="登录自动接管超时，保持固件自动控制。";
                            } else if(failure!=null) {
                                status.Text="登录启动：等待有效的 CPU/GPU 温度，最多 2 分钟。";
                            } else {
                                pendingAuto=false;
                                RunAction(()=> {
                                    if(File.Exists(Files.Marker))controller.Restore();
                                    var c=ReadConfig();c.Validate();
                                    controller.Enable(c);controller.Tick(snapshot);ShowSnapshot(snapshot);
                                    Files.Log("LOGIN STARTUP: saved curve accepted; 30-second sampling started.");
                                });
                            }
                        } else if(failure!=null) RunAction(()=>{controller.Average.Clear();throw failure;});
                        else RunAction(()=>{controller.Tick(snapshot);ShowSnapshot(snapshot);});
                        UpdateControls();
                    }));
                } catch {busy=false;}
            });
        }
        void HandleGpuFailure(Exception failure) {
            gpuRecovery.Fail(sensorClock.Elapsed.TotalSeconds,pendingAuto || (controller!=null && (controller.Active || controller.ScheduledPaused)));
            pendingAuto=false;
            bool released=false;
            RunAction(()=> {
                if(controller!=null) {
                    controller.Average.Clear();
                    if(controller.Active || controller.ScheduledPaused || guard.Armed.WaitOne(0) || File.Exists(Files.Marker))controller.Restore();
                }
                released=true;
            });
            int seconds=(int)Math.Ceiling(Math.Max(0,gpuRecovery.NextAttempt-sensorClock.Elapsed.TotalSeconds));
            readings.Text="温度读取暂不可用（未使用旧读数）";
            targetText.Text=released?"目标：系统自动控制":"目标：等待恢复系统控制";
            if(released)status.Text=String.Format("{0} {1} 秒后重试；连续 3 次有效读数后{2}。",failure.Message,seconds,gpuRecovery.ResumeRequested?"自动重新采样接管":"恢复监控");
            Files.Log("GPU_RETRY: "+failure.Message+" retrySeconds="+seconds+" resume="+gpuRecovery.ResumeRequested+" released="+released);
            UpdateControls();
        }
        void ShowSnapshot(Snapshot s) {
            readings.Text=String.Format("CPU {0}°C     GPU {1}°C     风扇 1：{2} RPM     风扇 2：{3} RPM",s.Cpu,s.Gpu,s.Fan1,s.Fan2);
            if(controller.Active && controller.LastTarget!=null) {
                targetText.Text=String.Format("目标：{0} / {1} RPM    均温+偏移：{2:F1}°C    更新：{3:T}",
                    controller.LastTarget[0],controller.LastTarget[1],Math.Max(controller.Average.Cpu,controller.Average.Gpu)+controller.Config.Offset,s.At);
                status.Text=controller.Note;
            } else {targetText.Text=(controller.SamplingFloorActive?"目标：采样最低转速保护    更新：":"目标：固件自动    更新：")+s.At.ToString("T"); if(controller.Active || controller.ScheduledPaused || controller.Note.Contains("偏离")) status.Text=controller.Note;}
        }
        void UpdateControls() {
            bool active=(controller!=null && (controller.Active || controller.ScheduledPaused)) || battery.ResumeRequested || pendingAuto || resumeAfterSleep || gpuRecovery.ResumeRequested;
            grid.Enabled=offset.Enabled=save.Enabled=!active;
            start.Enabled=controller!=null && !suspended && !battery.Paused && !active && !File.Exists(Files.Marker);
            stop.Enabled=controller!=null || active;
        }
        CurveConfig ReadConfig() {
            grid.EndEdit();
            var c=new CurveConfig {Offset=(int)offset.Value,Points=new List<PointConfig>()};
            foreach(DataGridViewRow row in grid.Rows) c.Points.Add(new PointConfig {
                Temperature=Convert.ToDouble(row.Cells[0].Value),
                Fan1=Convert.ToInt32(row.Cells[1].Value),Fan2=Convert.ToInt32(row.Cells[2].Value)});
            return c;
        }
        void LoadConfig() {
            CurveConfig c=CurveConfig.Default();
            try {if(File.Exists(Files.Config)) {c=new JavaScriptSerializer().Deserialize<CurveConfig>(File.ReadAllText(Files.Config));c.Validate();savedConfigValid=true;}}
            catch(Exception ex) {c=CurveConfig.Default();status.Text="配置无效，已载入默认值："+ex.Message;}
            offset.Value=c.Offset;
            foreach(PointConfig p in c.Points) grid.Rows.Add(p.Temperature,p.Fan1,p.Fan2);
            Plot(c);start.Enabled=stop.Enabled=false;
        }
        void SaveConfig(CurveConfig c) {
            c.Validate();
            string temporary=Files.Config+".tmp";
            File.WriteAllText(temporary,new JavaScriptSerializer().Serialize(c));
            if(File.Exists(Files.Config))File.Replace(temporary,Files.Config,null);
            else File.Move(temporary,Files.Config);
            savedConfigValid=true;
        }
        void Plot(CurveConfig c) {
            chart.Series[0].Points.Clear();chart.Series[1].Points.Clear();
            foreach(PointConfig p in c.Points) {chart.Series[0].Points.AddXY(p.Temperature,p.Fan1);chart.Series[1].Points.AddXY(p.Temperature,p.Fan2);}
        }
    }
    static class Program {
        [STAThread]
        static int Main(string[] args) {
            if(args.Length==3 && args[0]=="--watchdog") return Guard.Watch(int.Parse(args[1]),args[2]);
            if(args.Length>0 && args[0]=="--self-test") {
                var c=CurveConfig.Default();c.Validate();
                if(c.Target(54,47)[0]!=2800 || c.Target(54,47)[1]!=3100) return 1;
                if(c.Target(82,40)[0]!=4500 || c.Target(40,74)[1]!=4800) return 2;
                if(c.Target(10,10)[0]!=2000 || c.Target(100,90)[1]!=4800) return 3;
                var average=new TemperatureAverage();
                DateTime at=new DateTime(2026,1,1);
                for(int i=0;i<90;i++)average.Add(new Snapshot {Cpu=50,Gpu=40,At=at.AddSeconds(i*2)});
                int[] spike=average.Target(c,new Snapshot {Cpu=81,Gpu=40,At=at.AddSeconds(180)});
                if(average.Cpu>51 || spike[0]>=4000) return 5;
                if(average.Target(c,new Snapshot {Cpu=82,Gpu=40,At=at.AddSeconds(182)})[0]!=4500)return 6;
                for(int i=92;i<=182;i++)average.Add(new Snapshot {Cpu=70,Gpu=60,At=at.AddSeconds(i*2)});
                if(average.Cpu!=70 || average.Gpu!=60)return 7;
                average.Add(new Snapshot {Cpu=40,Gpu=45,At=at.AddSeconds(500)});
                if(average.Cpu!=40 || average.Gpu!=45)return 12;
                average.Clear();
                if(average.Target(c,new Snapshot {Cpu=40,Gpu=74,At=at})[1]!=4800)return 13;
                c.Offset=0;
                if(c.Target(81,40)[0]>=4500 || c.Target(40,73)[1]>=4800)return 14;
                if(c.Target(70,40)[0]>=4500 || c.Target(40,70)[1]>=4800)return 15;
                var cold=new TemperatureAverage();
                for(int i=0;i<90;i++)cold.Add(new Snapshot {Cpu=50,Gpu=40,At=at.AddSeconds(i*2)});
                if(cold.Target(c,new Snapshot {Cpu=81,Gpu=73,At=at.AddSeconds(180)})[0]>=4500)return 16;
                if(cold.Target(c,new Snapshot {Cpu=50,Gpu=74,At=at.AddSeconds(182)})[1]!=4800)return 17;
                if(c.Points.Count!=9 || c.Points[8].Temperature!=110)return 18;
                var invalid=CurveConfig.Default();invalid.Points.RemoveAt(4);
                bool missingRejected=false;try {invalid.Validate();}catch {missingRejected=true;}if(!missingRejected)return 19;
                invalid=CurveConfig.Default();invalid.Points[7].Fan1=4400;
                bool maxRejected=false;try {invalid.Validate();}catch {maxRejected=true;}if(!maxRejected)return 20;
                bool rejected=false;try {c.Points[2].Temperature=40;c.Validate();} catch {rejected=true;}
                if(!rejected)return 4;
                File.WriteAllText(Path.Combine(Files.Dir,"curve-self-test.txt"),"PASS: interpolation, positive offset, emergency maximum, endpoint clamp, invalid curve rejection, rolling 180-second window, boost smoothing, CPU82/GPU74 instantaneous override and below-threshold boundaries, 9 fixed nodes 30-110, startup and gap reset. No hardware access.");
                return 0;
            }
            if(args.Length>0 && (args[0]=="--hardware-test" || args[0]=="--watchdog-test")) {
                Backend.CheckMachine();
                bool testOwned;
                using(var testLock=new Mutex(true,@"Local\FanCurve21LE.Single",out testOwned)) {
                    if(!testOwned || File.Exists(Files.Marker)) return 8;
                    try {
                        using(var guard=new Guard(@"Local\FanCurve21LE."+Guid.NewGuid().ToString("N"))) {
                            guard.Start(); var backend=new Backend(); var initial=backend.ReadSnapshot();
                            if(initial.Cpu>80 || initial.Gpu>75 || initial.Fan1>3500 || initial.Fan2>3800) return 9;
                            if(args[0]=="--watchdog-test") {
                                Files.Mark();guard.Pulse.Set();guard.Armed.Set();
                                backend.Write(Backend.Fan1Id,3500);backend.Write(Backend.Fan2Id,3800);
                                Files.Log("WATCHDOG TEST: waiting without heartbeats.");
                                if(!guard.Fault.WaitOne(18000)) {backend.Auto();guard.Armed.Reset();Files.Clear();return 10;}
                                var wait=Stopwatch.StartNew();
                                while(File.Exists(Files.Marker) && wait.ElapsedMilliseconds<6000) Thread.Sleep(200);
                                if(File.Exists(Files.Marker)) {backend.Auto();guard.Armed.Reset();Files.Clear();return 11;}
                                File.WriteAllText(Path.Combine(Files.Dir,"curve-watchdog-test.txt"),"PASS: heartbeat loss triggered independent recovery; manual marker removed.");
                                return 0;
                            }
                            var controller=new Controller(backend,guard);
                            try {
                                var config=CurveConfig.Default(); controller.Enable(config); controller.Tick(initial);
                                // Exercise the same 30s sampling and 60s transition as the actual app.
                                var startupWait=Stopwatch.StartNew();
                                while(startupWait.Elapsed.TotalSeconds<94) {Thread.Sleep(2000);controller.Tick(backend.ReadSnapshot());}
                                int[] expected=(int[])controller.LastTarget.Clone();
                                Snapshot after=null;
                                for(int i=0;i<5;i++) {Thread.Sleep(2000); after=backend.ReadSnapshot();guard.Pulse.Set();}
                                if(Math.Abs(after.Fan1-expected[0])>500 || Math.Abs(after.Fan2-expected[1])>500)
                                    throw new Exception("RPM readback did not converge.");
                                File.WriteAllText(Path.Combine(Files.Dir,"curve-hardware-test.json"),
                                    new JavaScriptSerializer().Serialize(new {Before=initial,Target=expected,After=after}));
                            } finally {controller.Restore();}
                            return 0;
                        }
                    } finally {testLock.ReleaseMutex();}
                }
            }
            if(args.Length>0 && args[0]=="--startup-self-test") {
                try {return LoginStartup.SelfTest()?0:1;}
                catch(Exception ex) {File.WriteAllText(Path.Combine(Files.Dir,"startup-self-test.txt"),ex.ToString());return 1;}
            }
            bool smoke=args.Length>0 && args[0]=="--smoke";
            if(smoke) Application.ThreadException+=(sender,error)=> {File.WriteAllText(Path.Combine(Files.Dir,"curve-layout-error.txt"),error.Exception.ToString());Environment.Exit(1);};
            if(args.Length>0 && args[0]=="--restore-auto") {
                try {bool acquired; using(var lockout=new Mutex(true,@"Local\FanCurve21LE.Single",out acquired)) {
                    if(!acquired) {MessageBox.Show("请在正在运行的曲线窗口中点击恢复自动。"); return 2;}
                    try {Backend.CheckMachine();new Backend().Auto();Files.Clear();Files.Log("CLI restore commands sent.");return 0;}
                    finally {lockout.ReleaseMutex();}
                }}
                catch(Exception ex) {Files.Log(ex.ToString());return 1;}
            }
            bool owned;
            bool trayLaunch=args.Contains("--autostart") || args.Contains("--tray");
            using(var single=new Mutex(true,@"Local\FanCurve21LE.Single",out owned)) {
                if(!owned) {if(trayLaunch)return 0;MessageBox.Show("曲线程序已在运行。");return 1;}
                try {
                    Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
                    using(var form=new MainForm(smoke,args.Contains("--autostart"))) {
                        if(trayLaunch) {
                            using(var context=new TrayApplicationContext(form))Application.Run(context);
                        } else Application.Run(form);
                    }
                    return 0;
                } finally {single.ReleaseMutex();}
            }
        }
    }
}
