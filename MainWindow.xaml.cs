﻿using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Ports;
using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using System.Windows.Threading;
using System.Threading;
using System.Timers;
using System.Linq;

namespace RVDash;
public partial class MainWindow : Window
{
    protected Gauges gauges = new();
    private BlockingCollection<Msg> queue = new();
    private BlockingCollection<Err> errQueue = new();
    public static int OOWCnt = 0;
    private bool done = false;
    private Cameras cams = new Cameras(@"http://Camera:81/stream");
	ushort curResFuel = 0;
	private ulong savedTank = 0;
    private bool showVolts = false;
	private bool Ign = false;
    public MainWindow()
    {
		InitializeComponent();
        Task.Factory.StartNew(DumpErrs, TaskCreationOptions.LongRunning);
        this.Loaded += new RoutedEventHandler(Window_Loaded);
        this.MouseDoubleClick += Window_DBLClick;
        this.Closed += Window_Closed;
		savedTank = Properties.Settings.Default.CurTank;
    }
    void Window_Loaded(object sender, RoutedEventArgs e)
	{
		SerRead sECU = null,sVDC= null;
		//Set the current value of the gauges
		this.DataContext = gauges;
        CheckScreen();
        if (Environment.MachineName.Equals("dash", StringComparison.CurrentCultureIgnoreCase))
		{
			if (true)
			{
				sECU = new SerRead('E',5);
                sVDC = new SerRead('I',6);
			}
			else if (false)
			{
                sECU = new SerRead('E', 5, "binE.dat");
                sVDC = new SerRead('I', 6, "binV.dat");
			}
			else
			{
                sECU = new SerRead('E', "binE2.dat");
              //  sVDC = new SerRead('I', "binV2.dat");
			}
            Task.Factory.StartNew(() => readADC(2), TaskCreationOptions.LongRunning);
			Task.Factory.StartNew(() => readLoop(sECU), TaskCreationOptions.LongRunning);
			if (sVDC != null)
				Task.Factory.StartNew(() => readLoop(sVDC), TaskCreationOptions.LongRunning);
		}
		else
		{
			/*sECU = new SerRead('E', "binE.dat");
			sVDC = new SerRead('I',6, 10000, "binV2.dat");
			Task.Factory.StartNew(() => readADC(2), TaskCreationOptions.LongRunning);
			Task.Factory.StartNew(() => readLoop(sECU), TaskCreationOptions.LongRunning);
			if (sVDC != null)
				Task.Factory.StartNew(() => readLoop(sVDC), TaskCreationOptions.LongRunning);
            */
		}
		System.Timers.Timer aTimer = new System.Timers.Timer();
		aTimer.Elapsed += new ElapsedEventHandler(OnMinute);
		aTimer.Interval = 500;
		aTimer.Enabled = true;

        updateFuel();
	}
	private void OnMinute(object source, ElapsedEventArgs e)
	{
        gauges.Tick();
	}
	void Window_DBLClick(object sender, RoutedEventArgs e)
	{
        if (this.WindowStyle == WindowStyle.None)
        {
			this.WindowStyle = WindowStyle.ThreeDBorderWindow;
			this.WindowState = WindowState.Normal;
		}
		else
            CheckScreen();
	}
    private void DumpErrs()
    {
        using (StreamWriter sw = new StreamWriter(Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE"), "Downloads", "Err.txt")))
        {
            sw.AutoFlush = true;
			while (!done)
            {
                var e = errQueue.Take();
				string[] strs = new string[e.data.Length];
				string[] sums = new string[e.data.Length];
				byte sum = 0;
				for (int j = 0; j < e.data.Length; j++)
				{
                    byte v = e.data[j];
					sum += v;
					sums[j] = sum.ToString("D3");
					strs[j] = v.ToString("D3");
				}
				Console.WriteLine(string.Join(" ", strs));
				Console.WriteLine(string.Join(" ", sums));
                sw.WriteLine(string.Format("{0} {1} {2}: {3}", e.source, e.position, e.badPos, e.message));
				sw.WriteLine(String.Join(' ', strs));
				sw.WriteLine(String.Join(' ', sums));
			}
		}
    }
	void CheckScreen()
    {
		if (Screen.AllScreens.Length > 1)
		{
			foreach (Screen s in Screen.AllScreens)
			{
				if (!s.Primary)
				{
					var scaleRatio = Math.Max(Screen.PrimaryScreen.WorkingArea.Width / SystemParameters.PrimaryScreenWidth,
									Screen.PrimaryScreen.WorkingArea.Height / SystemParameters.PrimaryScreenHeight);
					this.Left = s.WorkingArea.Left / scaleRatio;
					this.Top = s.WorkingArea.Top / scaleRatio;
                    this.WindowStyle = WindowStyle.None;
                    this.WindowState = WindowState.Maximized;
					break;
				}
			}
		} else
        {
			this.WindowStyle = WindowStyle.ThreeDBorderWindow;
			this.WindowState = WindowState.Normal;
		}
	}
	void Window_Closed(object sender, System.EventArgs e)
    {
		Properties.Settings.Default.Save();
        cams.Close();
		done = true;
    }
    private void readADC(object port)
    {
        SerialPort sp = new SerialPort();
        sp.BaudRate = 115200;
        sp.PortName = string.Format("COM{0}",port);
        sp.Open();
        decimal Vr=1M;
        while (!done)
        {
            try
            {
                string line = sp.ReadLine();
                if (line.Length < 10) continue;
                ushort pid = ushort.Parse(line.Substring(2, 1));
                pid += 500;
                if (RemPIDs.Contains(pid))
                    continue;
                decimal dval = decimal.Parse(line.Substring(9, 5));
                if (pid == 504)
                {
                    Vr = dval;
                    continue;
                }
                if (pid == 509)
                {
                    decimal Vs = dval * 1094M / 100M;
                    if (!Ign && Vs > 5)
                    {
                        NativeMethods.PreventSleep();
                        queue.Add(new Msg('A', 127, 1, Ign));
                    }
                    else if (Ign && Vs < 5)
                    {
                        NativeMethods.AllowSleep();
						queue.Add(new Msg('A', 127, 1, Ign));
					}
					Ign = Vs > 5;
					if (Vr > 0 && Vs > 5 && Vs > Vr)
                        queue.Add(new Msg('A',140, 510, Vs / Vr));
                    continue;
                }
				Msg m = new Msg('A', 127, pid, Convert.ToUInt16(dval * 1000M));
                queue.Add(m);
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(DoUIChange));
            }
            catch { }
        }
	}
    private void readLoop(object sr)
    {
        SerRead serialPort = (SerRead)sr;
        bool outOfWhack = false;
        Dictionary<string, DateTime> msgs = new Dictionary<string, DateTime>();

        while (!done) // 1 message, many PID in each loop
        {
            byte MID;
            if (outOfWhack)
            {
                OOWCnt++;
                msgs.Clear();
                serialPort.Rewind();
                MID = serialPort.getNextOOW(ref done);
                outOfWhack = false;
            }
            else MID = serialPort.getNext(ref done);
            serialPort.Mark();
            byte sum = MID;
            object value = 0;
            int packetLen = 1;
            List<Msg> toSend = new List<Msg>();
            bool in255 = false;

            while (!outOfWhack)
            {
                byte rPid = serialPort.getNext(ref done);
                sum += rPid;
                packetLen++;
                if (sum == 0)
                {
                    byte nextMID = serialPort.peekNext(ref done);
                    if (nextMID == 128 || nextMID == 130 || nextMID == 136 || nextMID == 140)
                        break;
                }
                UInt16 pid = (UInt16)rPid;
                if (rPid == 255)
                {
                    if (packetLen > 1)
                    {

                        errQueue.Add(new Err(serialPort, "255 > 1"));
                        outOfWhack = true;
                        break;
                    }
                    in255 = true;
                    rPid = serialPort.getNext(ref done);
                    sum += rPid;
                    packetLen++;
                }
                if (in255)
                    pid = (UInt16)(rPid + 256);
                if (rPid < 128)
                {
                    byte b = serialPort.getNext(ref done);
                    sum += b;
                    value = b;
                    packetLen++;
                }
                else if (rPid < 192)
                {
                    byte b = serialPort.getNext(ref done);
                    sum += b;
                    byte c = serialPort.getNext(ref done);
                    sum += c;
                    value = (UInt16)(b + (256 * c));
                    packetLen += 2;
                }
                else if (rPid < 254)
                {
                    byte len = serialPort.getNext(ref done);
                    if (len != 0)
                    {
                        if (len > 21)
                        {
                            outOfWhack = true;
                            errQueue.Add(new Err(serialPort, string.Format("Bad len ({0})", len)));
                        }
                        else
                        {
                            sum += len;
                            byte[] buf = new byte[len];
                            value = buf;
                            for (int i = 0; i < len; i++)
                            {
                                buf[i] = serialPort.getNext(ref done);
                                sum += buf[i];
                            }
                            packetLen += len;
                        }
                    }
                }
                else if (rPid == 254)
                {
                    errQueue.Add(new Err(serialPort, "254"));
                    outOfWhack = true;
                }
                if (packetLen > 21)
                {
                    errQueue.Add(new Err(serialPort, string.Format("Too Long ({0})", packetLen)));
                    outOfWhack = true;
                }
                toSend.Add(new Msg(serialPort.source, serialPort.position, MID, pid, value));
            }
            if (!outOfWhack)
                foreach (Msg m in toSend)
                {
                    if (m.mid != 140 && InstPIDs.Contains(m.pid))
                        continue;
                    if (RemPIDs.Contains(m.pid) || RemMIDs.Contains(m.mid))
                        continue;
                    if (!msgs.ContainsKey(m.Code) || true || ((DateTime.Now - msgs[m.Code]).Milliseconds > 50))
                    {
                        msgs[m.Code] = DateTime.Now;
                        queue.Add(m);
                        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(DoUIChange));
                    }
                }

        }
    }
    private static HashSet<int> InstPIDs =>
        new HashSet<int>()
            { 84,96,100,102,110,117,118,168,177,190,245 };
	private static HashSet<int> RemPIDs =>
		new HashSet<int>()
			{ 0,1,2,3,9,24,70,71,83,89,91,92,128,151,187,191,194,244,500,501,502,503 };
	private static HashSet<int> RemMIDs =>
	    new HashSet<int>()
		    { 190,199,225,226 };
    private static string[] ILStr = { "Off", "On", "Err", "NA" };
	private void MPG_MouseDown(object sender, MouseButtonEventArgs e)
	{
		if (gauges.showmpg.Equals("Hidden")) gauges.showmpg = "Visble";
		else gauges.showmpg = "Hidden";
	}
	private void AvgMPG_MouseDown(object sender, MouseButtonEventArgs e)
	{
		Properties.Settings.Default.MPGGas = 0;
		Properties.Settings.Default.MPGMiles = curOdo;
	}
	bool openedWithButton = false;
    private void startCamera()
    {
		cams.Show();
		cams.Activate();
	}
	private void checkCamera()
    {
        if (gauges.transel.StartsWith("R"))
            startCamera();
        else if (!cams.IsVisible || openedWithButton)
            return;
        else cams.Hide();
    }
	private void Camera_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (gauges.transel.StartsWith("R"))
        {
            if (!cams.IsVisible)
            {
                startCamera();
                openedWithButton = false;
            }
        }
        else if (!cams.IsVisible)
        {
            startCamera();
            openedWithButton = true;
        }
        else
        {
            cams.Hide();
            openedWithButton = false;
        }
	}
	private void Volts_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (gauges.showvolts.Equals("Hidden"))
        {
            showVolts = true;
            gauges.showvolts = "Visble";
        }
        else
        {
            showVolts = false;
            gauges.showvolts = "Hidden";
        }
    }
    private void updateFuel()
	{
        //	gauges.fuel = (int)(Properties.Settings.Default.CurTank * 100L / Properties.Settings.Default.Tank);
        gauges.fuel = curResFuel;
		gauges.lowfuel = gauges.fuel < 15 ? "Yellow" : "Black";
		gauges.fuelvals = string.Format(" Fuel\r{0} {1}", curResFuel.ToString("D2"), ((int)(Properties.Settings.Default.CurTank * 100L / Properties.Settings.Default.Tank)).ToString("D2"));
	}

	private void Fuel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender.GetType() != typeof(CircularGaugeControl))
            return;
        CircularGaugeControl c = (CircularGaugeControl)sender;
        Point p = e.GetPosition(c);
        bool inCenter;
        var dv = c.GetValue(p,out inCenter);
		Properties.Settings.Default.CurTank = Properties.Settings.Default.Tank * (inCenter ? curResFuel : Convert.ToUInt64(dv)) / 100;
		updateFuel();
    }
    private Queue<ushort> fuelPct = new();
    private ushort fuelPctSum = 0;
    private uint curOdo=0;
	private DateTime lastFuel;
	private bool gotFuel = false;
	public void DoUIChange()
    {
        while (queue.Count > 0)
        {
            Msg m = queue.Take();

            int val = 0;
            Type t = m.value.GetType();
            if (t == typeof(byte) || t == typeof(UInt16))
                val = Convert.ToInt32(m.value);
            gauges.lowwater = "Hidden";
            switch (m.pid)
            {
                case 1: WindowState = Ign ? WindowState.Maximized : WindowState.Minimized; break;
                case 44: gauges.idiotlight = string.Format("{0}: Pro {1}, Amb {2}, Red {3}",m.MID, ILStr[(val >> 4) & 3], ILStr[(val >> 2) & 3], ILStr[val & 3]); break;
                case 40: gauges.retardersw = (val & 0x1) > 0 ? "Visible" : "Hidden"; break;
                case 47: gauges.retarder = (val & 0x3) > 0 ? "Visible" : "Hidden"; break;
                case 49: gauges.abs = (val & 0x3f) > 0 ? "Visible" : "Hidden"; break;
                // case 70: gauges.brake = (val & 0x80) > 0 ? "Red" : "DarkGray"; break;
                case 84: gauges.speed = val / 2; break;
                case 85: gauges.cruise = (val & 0x1) > 0 ? "Visible" : "Hidden"; gauges.cruiseact = (val & 0x80) != 0 ? "Visible" : "Hidden"; break;
                case 86: gauges.setspeed = val / 2; break;
                case 92: break; // pct engine load
                case 96: break; // gauges.fuel = val / 2; gauges.lowfuel = val < 30 ? "Yellow" : "Black"; break;
                case 100: gauges.oil = val / 2; gauges.lowoil = val < 20 ? "Yellow" : val < 10 ? "Red" : "Black"; break;
                case 108: break; // barometer
                case 102: gauges.boost = val / 8; break;
                case 105: gauges.inttemp = val; gauges.lowinttemp = val < 63 && gauges.rpm < 0.5M ? "cornflowerblue" : "Black"; break;
				case 110: gauges.water = val; gauges.hotwater = val > 210 ? "Yellow" : val > 220 ? "Red" : "Black"; break;
				case 111: gauges.lowwater = val > 10 ? "Visible" : "Hidden"; break;
				case 117: gauges.airPrim = val * 3 / 5; gauges.lowairprim = gauges.airPrim < 70 ? "Yellow" : "Black"; break;
                case 118: gauges.airSec = val * 3 / 5; gauges.lowairsec = gauges.airPrim < 70 ? "Yellow" : "Black"; break;
                case 121: break; // engine retarder
                case 130: break; // power specific fuel economy
                case 136: break; // brake vacuum
                case 164: break; // injection control pressure
                //2 byte
                case 162: gauges.transel = System.Text.Encoding.UTF8.GetString(BitConverter.GetBytes((UInt16)m.value));
                    checkCamera();
					break;
                case 163: var gear = System.Text.Encoding.UTF8.GetString(BitConverter.GetBytes((UInt16)m.value));
                    gauges.tranattain = gear.Replace("L", "");
                    break;
				case 168: { decimal v = (decimal)val * .05M;
                        gauges.volts = v.ToString("F1"); gauges.showvolts = v < 12.6M || showVolts ? "Visible" : "Hidden";
                        if (v < 12.6M) gauges.voltsBackground = "Red";
                        else gauges.voltsBackground = "Black";
						break;
					}
                case 177: gauges.transTemp = val / 4; break;
                case 183:
                    if (!gotFuel)
                    {
                        lastFuel = m.dt;
                        gotFuel = true;
                        break;
                    }
					TimeSpan s = m.dt - lastFuel;
                    lastFuel = m.dt;
                    ulong gasval = (ulong)val * Convert.ToUInt64(s.TotalMicroseconds);
                    if (gasval == 0)
                        break;
                    Properties.Settings.Default.MPGGas += gasval;
					gauges.avgfuel = ((curOdo - Properties.Settings.Default.MPGMiles) * 64M * 3600M * 100000M / Properties.Settings.Default.MPGGas).ToString("F1");

                    if (gasval > Properties.Settings.Default.CurTank)
                        Properties.Settings.Default.CurTank = 0;
                    else Properties.Settings.Default.CurTank -= gasval;
                    if (gasval == 0 && Properties.Settings.Default.CurTank != savedTank)
                    {
                        Properties.Settings.Default.Save();
                        savedTank = Properties.Settings.Default.CurTank;
                    }
					updateFuel();
                    break; // fuel rate (4.34 x 10-6gal/s or 1/64 gal/h)
                case 184: gauges.instfuel = ((decimal)val / 256M).ToString("F1"); break;
				case 185:  break; // avg fuel
				case 190: gauges.rpm = (decimal)val / 400; break;
                case 199: break; // traction control disable state
                //4 byte:
                case 225: break; // reserved for text message
                case 245: curOdo = BitConverter.ToUInt32((byte[])m.value); if (curOdo > 1000000) curOdo = 0; gauges.miles = (curOdo * .1M).ToString("F1");
                    if (Properties.Settings.Default.MPGMiles == 0 && curOdo > 0)
                    {
                        Properties.Settings.Default.MPGGas = 0;
						Properties.Settings.Default.MPGMiles = curOdo;
					}
					break;
				case 505: gauges.rightturn = val > 400 ? "Green" : "Black"; break;
				case 506: gauges.leftturn = val > 400 ? "Green" : "Black"; break;
				case 507: gauges.high = val > 400 ? "Blue" : "Black"; break;
				case 508: gauges.drawers = val < 400 && Ign ? "Visible" : "Hidden"; break;
				case 510:
                    decimal R = 770M / ((decimal)m.value - 1M);
                    decimal p = 129.1573M - (0.980531M * R) + (0.001846232M * R * R); // https://mycurvefit.com/ fit to 240=0, 148=.25, 100=.5, 60=.75, 33=1
                    if (p < 0) p = 0;
                    else if (p > 100) p = 100;
                    ushort fp = Convert.ToUInt16(p);
                    fuelPct.Enqueue(fp);
                    fuelPctSum += fp;
                    curResFuel = (ushort)(fuelPctSum / fuelPct.Count);
                    while (fuelPct.Count > 20)
                        fuelPctSum -= fuelPct.Dequeue();
                    updateFuel();
                    break;
				default:
					break;
            }
        }
    }
}
public class SerRead
{
    private SerialPort sp = null;
    private Dat d=null;
    public char source;
    public ulong position;
    byte readPos = 0, writePos = 0, markReadPos = 0;
    byte[] data = new byte[256];
    public SerRead(char n,int port)
    {
        source = n;
        OpenPort(port);
    }
    public SerRead(char n, string fn)
    {
        source = n;
		d = new Dat(fn);
    }
    public SerRead(char n, int port, string fn)
    {
        source = n;
		d = new Dat(fn,true);
        OpenPort(port);
    }
    private void OpenPort(int port)
    {
        sp = new SerialPort();
        sp.PortName = string.Format("COM{0}", port);
        sp.BaudRate = 9600;
//        sp.ReadTimeout = 10000;
        sp.Open();
    }    
    public void pause()
    {
        d.pause = true;
    }
    private void Read(ref bool done)
    {
        int amt;
        if (markReadPos <= writePos)
        {
            amt = 256 - writePos;
            if (markReadPos == 0) amt--;
        }
        else amt = markReadPos - writePos - 1;
        if (amt > 25) amt = 25;
        int len=0;
        if (sp != null)
        {
            try
            {
                len = sp.Read(data, writePos, amt);
                if (d != null)
                    d.add(data, writePos, len);
            }
            catch (IOException)
            {
                len = 0;
            }
        }
        else
        {
            len = d.read(data, writePos, amt);
            done = len == 0;
        }
        position += (ulong)len;
        writePos += (byte)len;
    }
    public byte peekNext(ref bool done)
    {
		while (!done && readPos == writePos)
			Read(ref done);
		if (!done) return data[readPos];
		if (d != null)
			d.Stop();
		return 0;
	}
	public byte getNext(ref bool done)
    {
        byte ret = peekNext(ref done);
        readPos++;
        return ret;
    }
    public void Mark()
    {
        markReadPos = readPos;
    }
    public void GetRewindBytes(out byte[] bytes,out ulong pos,out int rpos)
    {
        int len = 0;
        rpos = 0;
        byte mp = (byte)(markReadPos - 1);
        if (mp <= writePos)
            len = writePos - mp;
        else len = 256 - mp + writePos;
        bytes = new byte[len];
        int i = 0;
        for (byte r = mp; r != writePos; r++)
        {
            bytes[i++] = data[r];
            if (r == readPos) rpos = i;
        }
        pos = position - (ulong)len;
    }
    public void Rewind()
    {
        readPos = markReadPos;
    }
    public byte getNextOOW(ref bool done)
    {
        byte ret=0;
        while (!done && (ret = getNext(ref done)) < 128) ;
        return ret;
    }
}
public class Err
{
    public char source;
    public string message;
    public ulong position;
    public int badPos;
    public byte[] data;
    public Err(SerRead sp,string message)
    {
        this.source = sp.source;
        sp.GetRewindBytes(out data, out position, out badPos);
        this.message = message;
    }
}
public class Msg
{
    public byte mid;
    public UInt16 pid;
    public object value;
    public int cnt=1;
    public char source;
    public ulong pos;
    public bool cnts = true;
    public DateTime dt = DateTime.Now;
    public Msg(char source, byte mid, UInt16 pid, object value)
    {
        this.source=source;
        this.mid=mid;
        this.pid=pid;
        this.value=value;
        this.pos = 0L;
    }
    public Msg(char source,ulong pos,byte mid, UInt16 pid, object value)
    {
        this.source = source;
        this.mid = mid;
        this.pid = pid;
        this.value = value;
        this.pos= pos;
    }
    public override bool Equals(object obj)
    {
        var item = obj as Msg;
        if (item == null) return false;
        return this.mid == item.mid && this.pid == item.pid;
    }
    public bool LessThan(Msg other)
    {
        if (mid < other.mid) return true;
        if (mid > other.mid) return false;
        if (pid < other.pid) return true;
        if (pid > other.pid) return false;
        return true;
    }
    public string Cnts => cnts ? "N" : "O";
    public string Code => string.Format("{0} {1}", mid, pid);
    public string MID
    {
        get
        {
            if (IDMaps.MIDs.ContainsKey(mid)) return IDMaps.MIDs[mid];
            return mid.ToString();
        }
    }
    public string Src => source.ToString();
    public string PID
    {
        get
        {
            if (IDMaps.PIDs.ContainsKey(pid)) return IDMaps.PIDs[(byte)pid];
            return pid.ToString();
        }
    }
    public static string Str(byte[] a) => String.Join(',', a);
    public string Cnt => cnt.ToString();
    public string Data
    {
        get
        {
            if (value.GetType() == typeof(byte[]))
            {
                byte[] d = (byte[])value;
                if (pid == 226)
                {
                    string s = string.Empty;
                    for (int i=0;i<d.Length;i++)
                        if (d[i] != 0)
                            s += string.Format("{1}:{0} ", Convert.ToString(d[i], 2).PadLeft(8, '0'),i);
                    return s;

                }
                return Str(d);
            }
            return value.ToString();
        }
    }
}
public class Gauges : INotifyPropertyChanged
{
	// boiler-plate
	public event PropertyChangedEventHandler PropertyChanged;
    protected virtual void OnPropertyChanged(string propertyName)
    {
        PropertyChangedEventHandler handler = PropertyChanged;
        if (handler != null) handler(this, new PropertyChangedEventArgs(propertyName));
    }
    protected bool SetField<T>(ref T field, T value, string propertyName)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
	// props
	private string _volts;
    public string volts
    {
        get => _volts;
        set => SetField(ref _volts, value, "volts");
    }
    private string _idiotlight;
    public string idiotlight
	{
		get => _idiotlight;
		set => SetField(ref _idiotlight, value, "idiotlight");
	}
	private int _speed;
	public int speed
	{
		get => _speed;
		set => SetField(ref _speed, value, "speed");
	}
	private int _setspeed;
    public int setspeed
    {
        get => _setspeed;
        set => SetField(ref _setspeed, value, "setspeed");
    }
    private int _oil;
    public int oil
    {
        get => _oil;
        set => SetField(ref _oil, value, "oil");
    }
    private string _hotwater = "Black";
    public string hotwater
    {
        get => _hotwater;
        set => SetField(ref _hotwater, value, "hotwater");
    }
    private int _water;
    public int water
    {
        get => _water;
        set => SetField(ref _water, value, "water");
    }
	private string _avgfuel;
	public string avgfuel
	{
		get => _avgfuel;
		set => SetField(ref _avgfuel, value, "avgfuel");
	}
	private string _instfuel = "0";
	public string instfuel
	{
		get => _instfuel;
		set => SetField(ref _instfuel, value, "instfuel");
	}
	private decimal _rpm = 0;
	public decimal rpm
	{
		get => _rpm;
		set => SetField(ref _rpm, value, "rpm");
	}
	private int _airPrim = 0;
    public int airPrim
    {
        get => _airPrim;
        set => SetField(ref _airPrim, value, "airPrim");
    }
    private int _airSec = 0;
    public int airSec
    {
        get => _airSec;
        set => SetField(ref _airSec, value, "airSec");
    }
    private int _transTemp = 0;
    public int transTemp
    {
        get => _transTemp;
        set => SetField(ref _transTemp, value, "transTemp");
    }
    private int _boost = 0;
    public int boost
    {
        get => _boost;
        set => SetField(ref _boost, value, "boost");
    }
    private string _miles = "?";
    public string miles
    {
        get => _miles;
        set => SetField(ref _miles, value, "miles");
    }
    public void Tick()
    {
        OnPropertyChanged("clock");
    }
	public string clock
    {
        get { return DateTime.Now.ToString("t"); }
    }
	private string _transel = "N";
	public string transel
	{
		get => _transel;
		set => SetField(ref _transel, value, "transel");
	}
	private string _tranattain = "0";
	public string tranattain
	{
		get => _tranattain;
		set => SetField(ref _tranattain, value, "tranattain");
	}
	private string _retarder = "Hidden";
	public string retarder
	{
		get => _retarder;
		set => SetField(ref _retarder, value, "retarder");
	}
	private string _retardersw = "Hidden";
    public string retardersw
    {
        get => _retardersw;
        set => SetField(ref _retardersw, value, "retardersw");
    }
	private string _cruise = "Hidden";
	public string cruise
	{
		get => _cruise;
		set => SetField(ref _cruise, value, "cruise");
	}
	private string _cruiseact = "Hidden";
	public string cruiseact
	{
		get => _cruiseact;
		set => SetField(ref _cruiseact, value, "cruiseact");
	}
	private string _leftturn = "Black";
	public string leftturn
	{
		get => _leftturn;
		set => SetField(ref _leftturn, value, "leftturn");
	}
	private string _drawers = "Hidden";
	public string drawers
	{
		get => _drawers;
		set => SetField(ref _drawers, value, "drawers");
	}
	private string _high = "Black";
	public string high
	{
		get => _high;
		set => SetField(ref _high, value, "high");
	}
	private string _lowfuel = "Black";
	public string lowfuel
	{
		get => _lowfuel;
		set => SetField(ref _lowfuel, value, "lowfuel");
	}
	private string _lowinttemp = "Black";
	public string lowinttemp
	{
		get => _lowinttemp;
		set => SetField(ref _lowinttemp, value, "lowinttemp");
	}
	private string _rightturn = "Black";
    public string rightturn
    {
        get => _rightturn;
        set => SetField(ref _rightturn, value, "rightturn");
    }
	private string _lowoil = "Black";
	public string lowoil
	{
		get => _lowoil;
		set => SetField(ref _lowoil, value, "lowoil");
	}
	private string _lowairprim = "Black";
	public string lowairprim
	{
		get =>  _lowairprim;
		set => SetField(ref _lowairprim, value, "lowairprim");
	}
	private string _lowairsec = "Black";
	public string lowairsec
	{
		get => _lowairsec;
		set => SetField(ref _lowairsec, value, "lowairsec");
	}
    /*
    public uint brightness
    {
        get => bc.Get();
        set => bc.Set(value);
    }*/
    private string _lowwater = "Hidden";
    public string lowwater
    {
        get => _lowwater;
        set => SetField(ref _lowwater, value, "lowwater");
    }
    private string _showmpg = "Hidden";
    public string showmpg
    {
        get => _showmpg;
        set => SetField(ref _showmpg, value, "showmpg");
    }
    private string _voltsBackground = "black";
    public string voltsBackground { get { return _voltsBackground; } set { SetField(ref _voltsBackground, value, "voltsBackground"); } }
    private string _showvolts = "Hidden";
    public string showvolts
    {
        get => _showvolts;
        set => SetField(ref _showvolts, value, "showvolts");
    }
    private string _abs = "Hidden";
    public string abs
    {
        get => _abs;
        set => SetField(ref _abs, value, "abs");
    }
    private string _stopeng = "";
    public string stopeng
    {
        get => _stopeng;
        set => SetField(ref _stopeng, value, "stopeng");
    }
    private string _checkeng = "";
    public string checkeng
    {
        get => _checkeng;
        set => SetField(ref _checkeng, value, "checkeng");
    }
    private string _engprot = "";
    public string engprot
    {
        get => _engprot;
        set => SetField(ref _engprot, value, "engprot");
    }
	private string _fuelvals = "";
	public string fuelvals
	{
		get => _fuelvals;
		set => SetField(ref _fuelvals, value, "fuelvals");
	}
	private int _fuel;
	public int fuel
	{
		get => _fuel;
		set => SetField(ref _fuel, value, "fuel");
	}
	private int _inttemp;
    public int inttemp
    {
        get => _inttemp;
        set => SetField(ref _inttemp, value, "inttemp");
    }
}
public class Dat
{
    private FileStream fs = null;
    private BlockingCollection<byte[]> writeQueue = new BlockingCollection<byte[]>();
    public bool pause = false;
    private string getFn(string fn)
    {
        return Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE"), "Downloads", fn);
    }
    public Dat(string fn)
    {
        string fileName = getFn(fn);
        fs = File.Open(fileName, FileMode.Open);
    }
    private void writer()
    {
        while(true)
        {
            try
            {
                var buf = writeQueue.Take();
				fs.Write(buf, 0, buf.Length);
			}
			catch (InvalidOperationException)
            {
                fs.Close();
                return;
            }
        }
    }
    public void Stop()
    {
        writeQueue.CompleteAdding();
    }
    public Dat(string fn, bool saveTime)
    {
        fs = File.Open(getFn(fn), FileMode.Create);
		Task.Factory.StartNew(writer, TaskCreationOptions.LongRunning);
	}
	public void add(byte[] data, byte writePos, int len)
    {
        var b = new byte[len];
        Array.Copy(data, writePos, b, 0, len);
        writeQueue.Add(b);
    }
    private DateTime st = DateTime.Now;
    private int cur = 0;
    public int read(byte[] outbuf, int offset, int count)
    {
        int ret = fs.Read(outbuf, offset, count);
        cur += ret;
		var diff = DateTime.Now - st;
		int mils = cur - (int)(diff.TotalMilliseconds * 4);
		if (mils > 0)
			Thread.Sleep(mils);
        
		return ret;
    }
}
internal static class NativeMethods
{
	public static void PreventSleep()
	{
		SetThreadExecutionState(ExecutionState.EsDisplayRequired | ExecutionState.EsContinuous | ExecutionState.EsSystemRequired);
	}
	public static void AllowSleep()
	{
		SetThreadExecutionState(ExecutionState.EsContinuous);
	}
	[DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
	private static extern ExecutionState SetThreadExecutionState(ExecutionState esFlags);
	[FlagsAttribute]
	private enum ExecutionState : uint
	{
		EsAwaymodeRequired = 0x00000040,
		EsContinuous = 0x80000000,
		EsDisplayRequired = 0x00000002,
		EsSystemRequired = 0x00000001
	}
}
/*
public class GammaThing
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("gdi32.dll")]
    private static extern bool SetDeviceGammaRamp(IntPtr hDC, ref RAMP lpRamp);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct RAMP
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public UInt16[] Red;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public UInt16[] Green;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 256)]
        public UInt16[] Blue;
    }
    private uint val = 0;
    public uint Get() => val;
    public void Set(uint gamma)
    {
        val = gamma;
        if (gamma <= 256 && gamma >= 1)
        {
            RAMP ramp = new RAMP();
            ramp.Red = new ushort[256];
            ramp.Green = new ushort[256];
            ramp.Blue = new ushort[256];
            for (uint i = 1; i < 256; i++)
            {
                uint iArrayValue = i * (gamma + 128);

                if (iArrayValue > 65535)
                    iArrayValue = 65535;
                ramp.Red[i] = ramp.Blue[i] = ramp.Green[i] = (ushort)iArrayValue;
            }
            SetDeviceGammaRamp(GetDC(IntPtr.Zero), ref ramp);
        }
    }
}

public class PhysicalMonitorBrightnessController : IDisposable
{
    #region DllImport
    [DllImport("dxva2.dll", EntryPoint = "GetNumberOfPhysicalMonitorsFromHMONITOR")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, ref uint pdwNumberOfPhysicalMonitors);

    [DllImport("dxva2.dll", EntryPoint = "GetPhysicalMonitorsFromHMONITOR")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr hMonitor, uint dwPhysicalMonitorArraySize, [Out] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("dxva2.dll", EntryPoint = "GetMonitorBrightness")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorBrightness(IntPtr handle, ref uint minimumBrightness, ref uint currentBrightness, ref uint maxBrightness);

    [DllImport("dxva2.dll", EntryPoint = "SetMonitorBrightness")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMonitorBrightness(IntPtr handle, uint newBrightness);

    [DllImport("dxva2.dll", EntryPoint = "DestroyPhysicalMonitor")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitor(IntPtr hMonitor);

    [DllImport("dxva2.dll", EntryPoint = "DestroyPhysicalMonitors")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyPhysicalMonitors(uint dwPhysicalMonitorArraySize, [In] PHYSICAL_MONITOR[] pPhysicalMonitorArray);

    [DllImport("user32.dll")]
    static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, EnumMonitorsDelegate lpfnEnum, IntPtr dwData);
    delegate bool EnumMonitorsDelegate(IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData);
    #endregion

    private IReadOnlyCollection<MonitorInfo> Monitors
    {
        get; set;
    }
    public PhysicalMonitorBrightnessController()
    {
        UpdateMonitors();
    }
    #region Get & Set
    public void Set(uint brightness)
    {
        Set(brightness, true);
    }
    private void Set(uint brightness, bool refreshMonitorsIfNeeded)
    {
        bool isSomeFail = false;
        foreach (var monitor in Monitors)
        {
            uint realNewValue = (monitor.MaxValue - monitor.MinValue) * brightness / 100 + monitor.MinValue;
            if (SetMonitorBrightness(monitor.Handle, realNewValue))
            {
                monitor.CurrentValue = realNewValue;
            }
            else if (refreshMonitorsIfNeeded)
            {
                isSomeFail = true;
                break;
            }
        }

        if (refreshMonitorsIfNeeded && (isSomeFail || !Monitors.Any()))
        {
            UpdateMonitors();
            Set(brightness, false);
            return;
        }
    }
    public int Get()
    {
        if (!Monitors.Any())
        {
            return -1;
        }
        return (int)Monitors.Average(d => d.CurrentValue);
    }
    #endregion

    private void UpdateMonitors()
    {
        DisposeMonitors(this.Monitors);

        var monitors = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref Rect lprcMonitor, IntPtr dwData) =>
        {
            uint physicalMonitorsCount = 0;
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(hMonitor, ref physicalMonitorsCount))
            {
                // Cannot get monitor count
                return true;
            }

            var physicalMonitors = new PHYSICAL_MONITOR[physicalMonitorsCount];
            if (!GetPhysicalMonitorsFromHMONITOR(hMonitor, physicalMonitorsCount, physicalMonitors))
            {
                // Cannot get physical monitor handle
                return true;
            }

            foreach (PHYSICAL_MONITOR physicalMonitor in physicalMonitors)
            {
                uint minValue = 0, currentValue = 0, maxValue = 0;
                if (!GetMonitorBrightness(physicalMonitor.hPhysicalMonitor, ref minValue, ref currentValue, ref maxValue))
                {
                    DestroyPhysicalMonitor(physicalMonitor.hPhysicalMonitor);
                    continue;
                }

                var info = new MonitorInfo
                {
                    Handle = physicalMonitor.hPhysicalMonitor,
                    MinValue = minValue,
                    CurrentValue = currentValue,
                    MaxValue = maxValue,
                };
                monitors.Add(info);
            }

            return true;
        }, IntPtr.Zero);

        this.Monitors = monitors;
    }
    public void Dispose()
    {
        DisposeMonitors(Monitors);
        GC.SuppressFinalize(this);
    }
    private static void DisposeMonitors(IEnumerable<MonitorInfo> monitors)
    {
        if (monitors?.Any() == true)
        {
            PHYSICAL_MONITOR[] monitorArray = monitors.Select(m => new PHYSICAL_MONITOR { hPhysicalMonitor = m.Handle }).ToArray();
            DestroyPhysicalMonitors((uint)monitorArray.Length, monitorArray);
        }
    }
    #region Classes
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    public struct PHYSICAL_MONITOR
    {
        public IntPtr hPhysicalMonitor;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szPhysicalMonitorDescription;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int left;
        public int top;
        public int right;
        public int bottom;
    }
    public class MonitorInfo
    {
        public uint MinValue
        {
            get; set;
        }
        public uint MaxValue
        {
            get; set;
        }
        public IntPtr Handle
        {
            get; set;
        }
        public uint CurrentValue
        {
            get; set;
        }
    }
    #endregion
}
*/