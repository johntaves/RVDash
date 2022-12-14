using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.IO.Ports;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Forms;
using System.Collections;
using System.Windows.Input;
using System.Runtime.InteropServices;
using System.Security.Permissions;

namespace RVDash;

public partial class MainWindow : Window
{
    protected Gauges gauges = new();
    private Queue<Msg> queue = new Queue<Msg>();
    public static int OOWCnt = 0;
    private bool done = false;
    private MsgListWindow mlw;
    private Cameras cams = null;
	ushort curResFuel = 0;
	private ulong savedTank = 0;
    private bool showVolts = false;
    private double curSpeed=0;
	private SerRead capt1 = null, capt2 = null;
    private bool Ign = false;
    public MainWindow()
    {
        InitializeComponent();
        this.Loaded += new RoutedEventHandler(Window_Loaded);
        this.MouseDoubleClick += Window_DBLClick;
        this.Closed += Window_Closed;
		mlw = new MsgListWindow();
        mlw.Show();
        savedTank = Properties.Settings.Default.CurTank;
    }
	void Window_Loaded(object sender, RoutedEventArgs e)
	{
		Thread tECU = new Thread(readLoop);
		Thread tVDC = new Thread(readLoop);
		Thread tADC = new Thread(readADC);
		//Set the current value of the gauges
		this.DataContext = gauges;
        CheckScreen();
		if (Environment.MachineName.Equals("dash", StringComparison.CurrentCultureIgnoreCase))
		{
			if (true)
			{
				tECU.Start(new SerRead('E',10));
				tVDC.Start(new SerRead('I',6));
			}
			else if (false)
			{
				tECU.Start(capt1 = new SerRead('E', 10, 70000, "binE.dat"));
				tVDC.Start(capt2 = new SerRead('I', 6, 70000, "binV.dat"));
			}
			else
			{
				tECU.Start(capt1 = new SerRead('E', "binE.dat"));
				tVDC.Start(capt2 = new SerRead('I', "binV.dat"));
			}
			tADC.Start(8);
		}
		else
		{
			tECU.Start(capt1 = new SerRead('E', "binE.dat"));
			//tVDC.Start(capt2 = new SerRead('I',6, 10000, "binV2.dat"));
		}
		gauges.showcapt = capt1 != null ? "Visible" : "Hidden";
        updateFuel();
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
        done = true;
        mlw.Close();
    }
    private void readADC(object port)
    {
        NativeMethods.PreventSleep();
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
                    Ign = Vs > 5;
					if (Vr > 0 && Vs > 5 && Vs > Vr)
                        lock (queue) queue.Enqueue(new Msg('A',140, 510, Vs / Vr));
                    continue;
                }
				Msg m = new Msg('A', 127, pid, Convert.ToUInt16(dval * 1000M));
                lock (queue) queue.Enqueue(m);
                Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(DoUIChange));
            }
            catch { }
        }
	}
    private void readLoop(object sr)
    {
        NativeMethods.PreventSleep();
        SerRead serialPort = (SerRead)sr;
        bool outOfWhack = false;
        Dictionary<string,DateTime> msgs = new Dictionary<string,DateTime>();

        while (!done) // 1 message, many PID in each loop
        {
            byte MID;
            if (outOfWhack)
            {
                OOWCnt++;
                serialPort.Rewind();
                MID = serialPort.getNextOOW(ref done);
                outOfWhack = false;
            }
            else MID = serialPort.getNext(ref done);
            serialPort.Mark();
            byte sum = MID;
            object value = 0;
            int packetLen = 0;
            List<Msg> toSend = new List<Msg>();

            while (!outOfWhack)
            {
                byte rPid = serialPort.getNext(ref done);
                sum += rPid;
                packetLen++;
                if (sum == 0)
                    break;
                UInt16 pid = (UInt16)rPid;
                if (rPid == 255)
                {
                    rPid = serialPort.getNext(ref done);
                    sum += rPid;
                    pid = (UInt16)(rPid + 256);
                    packetLen++;
                }
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
                else // 254, 510, 511
                    outOfWhack = true;
                if (packetLen > 21)
                    outOfWhack = true;
				toSend.Add(new Msg(serialPort.name,MID, pid, value));
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
                        lock (queue) queue.Enqueue(m);
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
			{ 0,1,2,3,70,71,83,89,91,92,151,187,191,194,244,500,501,502,503 };
	private static HashSet<int> RemMIDs =>
	new HashSet<int>()
		{ 190,199,225,226 };
	private static HashSet<int> okPIDs =>
        new HashSet<int>() { 199, 225 };
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
	bool openWithButton = false;
	private void Camera_Closed()
    {
        cams = null;
        openWithButton = false;
    }
    private void toggleCamera(bool fromButton,bool isR = false)
    {
		if (cams == null)
		{
            if (fromButton)
                openWithButton = true;
            else if (!isR)
                return;
			cams = new Cameras(Camera_Closed);
			cams.Show();
		}
		else
		{
            if (fromButton)
                openWithButton = true;
            if (fromButton || isR)
                cams.Activate();
            if (!openWithButton && !fromButton && !isR)
    			cams.Close();
		}
	}
	private void Camera_MouseDown(object sender, MouseButtonEventArgs e)
    {
        toggleCamera(true);
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
        while (true)
        {
            Msg m = null;
            lock (queue)
            {
                if (queue.Count == 0)
                    return;
                m = queue.Dequeue();
            }
            gauges.errs = OOWCnt;
            int val = 0;
            Type t = m.value.GetType();
            if (t == typeof(byte) || t == typeof(UInt16))
                val = Convert.ToInt32(m.value);
            if (capt1 != null)
                gauges.captpos1 = capt1.CaptPos();
            if (capt2 != null)
                gauges.captpos2 = capt2.CaptPos();
            gauges.lowwater = "Hidden";
            //mlw.AddToList(m);
            switch (m.pid)
            {
                case 44: gauges.idiotlight = string.Format("{0}: Pro {1}, Amb {2}, Red {3}",m.MID, ILStr[(val >> 4) & 3], ILStr[(val >> 2) & 3], ILStr[val & 3]); break;
                case 40: gauges.retardersw = (val & 0x1) > 0 ? "Visible" : "Hidden"; break;
                case 47: gauges.retarder = (val & 0x3) > 0 ? "Visible" : "Hidden"; break;
                case 49: gauges.abs = (val & 0x3f) > 0 ? "Visible" : "Hidden"; break;
                // case 70: gauges.brake = (val & 0x80) > 0 ? "Red" : "DarkGray"; break;
                case 84: gauges.speed = val / 2; curSpeed = (double)val / 2; break;
                case 85: gauges.cruise = (val & 0x1) > 0 ? "Visible" : "Hidden"; gauges.cruiseact = (val & 0x80) != 0 ? "Visible" : "Hidden"; break;
                case 86: gauges.setspeed = val / 2; break;
                case 92: break; // pct engine load
                case 96: break; // gauges.fuel = val / 2; gauges.lowfuel = val < 30 ? "Yellow" : "Black"; break;
                case 100: gauges.oil = val / 2; gauges.lowoil = val < 20 ? "Yellow" : val < 10 ? "Red" : "Black"; break;
                case 108: break; // barometer
                case 102: gauges.boost = val / 8; break;
                case 105: gauges.inttemp = val; gauges.lowinttemp = val < 70 && gauges.rpm < 0.5M ? "cornflowerblue" : "Black"; break;
                case 110: gauges.water = val; gauges.hotwater = val > 217 ? "Yellow" : val > 225 ? "Red": "Black"; break;
                case 117: gauges.airPrim = val * 3 / 5; gauges.lowairprim = gauges.airPrim < 70 ? "Yellow" : "Black"; break;
                case 118: gauges.airSec = val * 3 / 5; gauges.lowairsec = gauges.airPrim < 70 ? "Yellow" : "Black"; break;
                case 121: break; // engine retarder
                case 164: break; // injection control pressure
                //2 byte
                case 162: gauges.transel = System.Text.Encoding.UTF8.GetString(BitConverter.GetBytes((UInt16)m.value));
                    toggleCamera(false, gauges.transel.StartsWith("R"));
					break;
                case 163: gauges.tranattain = System.Text.Encoding.UTF8.GetString(BitConverter.GetBytes((UInt16)m.value)); break;
				case 168: { decimal v = (decimal)val * .05M;
                        gauges.volts = v.ToString("F1"); gauges.showvolts = v < 12.6M || showVolts ? "Visible" : "Hidden"; break;
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
					mlw.AddToList(new Msg('C', 127, 604, Properties.Settings.Default.CurTank));
					updateFuel();
                    break; // fuel rate (4.34 x 10-6gal/s or 1/64 gal/h)
                case 184: gauges.instfuel = ((decimal)val / 256M).ToString("F1"); break;
				case 185:  break; // avg fuel
				case 190: gauges.rpm = (decimal)val / 400; break;
                //4 byte:
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
                case 508: gauges.drawers = val < 400 && Ign ? "Red" : "Black"; break;
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
                    mlw.AddToList(m);
                    if (!okPIDs.Contains(m.pid))
						gauges.msgs = "Visible";
					break;
            }
        }
    }
}
public class SerRead
{
    private SerialPort sp = null;
    private Dat d;
    public char name;
    byte readPos = 0, writePos = 0;
    byte[] data = new byte[256];
    public SerRead(char n,int port)
    {
        name = n;
        OpenPort(port);
    }
    public SerRead(char n, string fn)
    {
		name = n;
		d = new Dat(fn);
    }
    public SerRead(char n, int port, int size, string fn)
    {
		name = n;
		d = new Dat(size, fn);
        OpenPort(port);
    }
    private void OpenPort(int port)
    {
        sp = new SerialPort();
        sp.PortName = string.Format("COM{0}", port);
        sp.BaudRate = 9600;
        sp.ReadTimeout = 1000;
        sp.Open();
    }
	public int CaptPos() => d.CaptPos();
    
    public void pause()
    {
        d.pause = true;
    }
    private int ReadCapt(byte[] buf, int offset, int count)
    {
        int ret = 0;
        while (ret < count)
        {
            int i = sp.Read(buf, offset, 1);
            if (i == 1)
                d.add(buf[offset]);
            offset += i;
            ret += i;
        }
        return ret;
    }
    private void Read(ref bool done)
    {
        int amt;
        if (readPos <= writePos)
        {
            amt = 256 - writePos;
            if (readPos == 0) amt--;
        }
        else amt = readPos - writePos - 1;
        if (amt > 25) amt = 25;
        int len=0;
        if (sp != null)
        {
            try
            {
                if (d == null)
                    len = sp.Read(data, writePos, amt);
                else if (sp != null && d != null)
                    len = ReadCapt(data, writePos, amt);
            } catch (IOException)
            {
                len = 0;
            }
        }
        else
        {
            len = d.read(data, writePos, amt);
            done = len == 0;
        }
        writePos += (byte)len;
    }
    public byte getNext(ref bool done)
    {
        while (!done && readPos == writePos)
            Read(ref done);
        if (done) return 0;
        return data[readPos++];
    }
    private byte markReadPos;
    public void Mark()
    {
        markReadPos = readPos;
    }
    public void Rewind()
    {
        readPos = markReadPos;
    }
    public byte getNextOOW(ref bool done)
    {
        byte ret=0;
        getNext(ref done);
        while (!done && (ret = getNext(ref done)) < 128) ;
        return ret;
    }
}
public class Msg
{
    public byte mid;
    public UInt16 pid;
    public object value;
    public int cnt=1;
    public char source;
    public DateTime dt = DateTime.Now;
    public Msg(char n,byte m, UInt16 p, object v)
    {
        source = n;
        mid = m;
        pid = p;
        value = v;
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
        get
        {
            return _volts;
        }
        set
        {
            SetField(ref _volts, value, "volts");
        }
    }
    private string _idiotlight;
    public string idiotlight
	{
		get
		{
			return _idiotlight;
		}
		set
		{
			SetField(ref _idiotlight, value, "idiotlight");
		}
	}
	private int _captpos1;
	public int captpos1
	{
		get
		{
			return _captpos1;
		}
		set
		{
			SetField(ref _captpos1, value, "captpos1");
		}
	}
	private int _captpos2;
	public int captpos2
	{
		get
		{
			return _captpos2;
		}
		set
		{
			SetField(ref _captpos2, value, "captpos2");
		}
	}
	private int _speed;
	public int speed
	{
		get
		{
			return _speed;
		}
		set
		{
			SetField(ref _speed, value, "speed");
		}
	}
	private int _setspeed;
    public int setspeed
    {
        get
        {
            return _setspeed;
        }
        set
        {
            SetField(ref _setspeed, value, "setspeed");
        }
    }
    private int _oil;
    public int oil
    {
        get
        {
            return _oil;
        }
        set
        {
            SetField(ref _oil, value, "oil");
        }
    }
    private string _msgs = "Hidden";
    public string msgs
    {
        get
        {
            return _msgs;
        }
        set
        {
            SetField(ref _msgs, value, "msgs");
        }
    }
    private string _hotwater;
    public string hotwater
    {
        get
        {
            return _hotwater;
        }
        set
        {
            SetField(ref _hotwater, value, "hotwater");
        }
    }
    private int _water;
    public int water
    {
        get
        {
            return _water;
        }
        set
        {
            SetField(ref _water, value, "water");
        }
    }
	private string _avgfuel;
	public string avgfuel
	{
		get
		{
			return _avgfuel;
		}
		set
		{
			SetField(ref _avgfuel, value, "avgfuel");
		}
	}
	private string _instfuel;
	public string instfuel
	{
		get
		{
			return _instfuel;
		}
		set
		{
			SetField(ref _instfuel, value, "instfuel");
		}
	}
	private decimal _rpm;
	public decimal rpm
	{
		get
		{
			return _rpm;
		}
		set
		{
			SetField(ref _rpm, value, "rpm");
		}
	}
	private int _airPrim;
    public int airPrim
    {
        get
        {
            return _airPrim;
        }
        set
        {
            SetField(ref _airPrim, value, "airPrim");
        }
    }
    private int _airSec;
    public int airSec
    {
        get
        {
            return _airSec;
        }
        set
        {
            SetField(ref _airSec, value, "airSec");
        }
    }
    private int _transTemp;
    public int transTemp
    {
        get
        {
            return _transTemp;
        }
        set
        {
            SetField(ref _transTemp, value, "transTemp");
        }
    }
    private int _boost;
    public int boost
    {
        get
        {
            return _boost;
        }
        set
        {
            SetField(ref _boost, value, "boost");
        }
    }
    private string _miles;
    public string miles
    {
        get
        {
            return _miles;
        }
        set
        {
            SetField(ref _miles, value, "miles");
        }
    }
	private string _transel;
	public string transel
	{
		get
		{
			return _transel;
		}
		set
		{
			SetField(ref _transel, value, "transel");
		}
	}
	private string _tranattain;
	public string tranattain
	{
		get
		{
			return _tranattain;
		}
		set
		{
			SetField(ref _tranattain, value, "tranattain");
		}
	}
	private string _retarder;
	public string retarder
	{
		get
		{
			return _retarder;
		}
		set
		{
			SetField(ref _retarder, value, "retarder");
		}
	}
	private string _retardersw;
    public string retardersw
    {
        get
        {
            return _retardersw;
        }
        set
        {
            SetField(ref _retardersw, value, "retardersw");
        }
    }
    private string _wait;
    public string wait
    {
        get
        {
            return _wait;
        }
        set
        {
            SetField(ref _wait, value, "wait");
        }
    }
    private string _brake;
    public string brake
    {
        get
        {
            return _brake;
        }
        set
        {
            SetField(ref _brake, value, "brake");
        }
    }
	private string _showcapt;
	public string showcapt
	{
		get
		{
			return _showcapt;
		}
		set
		{
			SetField(ref _showcapt, value, "showcapt");
		}
	}
	private string _cruise;
	public string cruise
	{
		get
		{
			return _cruise;
		}
		set
		{
			SetField(ref _cruise, value, "cruise");
		}
	}
	private string _cruiseact;
	public string cruiseact
	{
		get
		{
			return _cruiseact;
		}
		set
		{
			SetField(ref _cruiseact, value, "cruiseact");
		}
	}
	private string _leftturn;
	public string leftturn
	{
		get
		{
			return _leftturn;
		}
		set
		{
			SetField(ref _leftturn, value, "leftturn");
		}
	}
	private string _drawers;
	public string drawers
	{
		get
		{
			return _drawers;
		}
		set
		{
			SetField(ref _drawers, value, "drawers");
		}
	}
	private string _high;
	public string high
	{
		get
		{
			return _high;
		}
		set
		{
			SetField(ref _high, value, "high");
		}
	}
	private string _lowfuel;
	public string lowfuel
	{
		get
		{
			return _lowfuel;
		}
		set
		{
			SetField(ref _lowfuel, value, "lowfuel");
		}
	}
	private string _lowinttemp;
	public string lowinttemp
	{
		get
		{
			return _lowinttemp;
		}
		set
		{
			SetField(ref _lowinttemp, value, "lowinttemp");
		}
	}
	private string _rightturn;
    public string rightturn
    {
        get
        {
            return _rightturn;
        }
        set
        {
            SetField(ref _rightturn, value, "rightturn");
        }
    }
	private string _lowoil;
	public string lowoil
	{
		get
		{
			return _lowoil;
		}
		set
		{
			SetField(ref _lowoil, value, "lowoil");
		}
	}
	private string _lowairprim;
	public string lowairprim
	{
		get
		{
			return _lowairprim;
		}
		set
		{
			SetField(ref _lowairprim, value, "lowairprim");
		}
	}
	private string _lowairsec;
	public string lowairsec
	{
		get
		{
			return _lowairsec;
		}
		set
		{
			SetField(ref _lowairsec, value, "lowairsec");
		}
	}
    private string _lowwater;
    public string lowwater
    {
        get
        {
            return _lowwater;
        }
        set
        {
            SetField(ref _lowwater, value, "lowwater");
        }
    }
    private string _showmpg = "Hidden";
    public string showmpg
    {
        get
        {
            return _showmpg;
        }
        set
        {
            SetField(ref _showmpg, value, "showmpg");
        }
    }
    private string _showvolts = "Hidden";
    public string showvolts
    {
        get
        {
            return _showvolts;
        }
        set
        {
            SetField(ref _showvolts, value, "showvolts");
        }
    }
    private string _abs;
    public string abs
    {
        get
        {
            return _abs;
        }
        set
        {
            SetField(ref _abs, value, "abs");
        }
    }
    private string _stopeng;
    public string stopeng
    {
        get
        {
            return _stopeng;
        }
        set
        {
            SetField(ref _stopeng, value, "stopeng");
        }
    }
    private string _checkeng;
    public string checkeng
    {
        get
        {
            return _checkeng;
        }
        set
        {
            SetField(ref _checkeng, value, "checkeng");
        }
    }
    private string _engprot;
    public string engprot
    {
        get
        {
            return _engprot;
        }
        set
        {
            SetField(ref _engprot, value, "engprot");
        }
    }
	private string _fuelvals;
	public string fuelvals
	{
		get
		{
			return _fuelvals;
		}
		set
		{
			SetField(ref _fuelvals, value, "fuelvals");
		}
	}
	private int _fuel;
	public int fuel
	{
		get
		{
			return _fuel;
		}
		set
		{
			SetField(ref _fuel, value, "fuel");
		}
	}
	private int _inttemp;
    public int inttemp
    {
        get
        {
            return _inttemp;
        }
        set
        {
            SetField(ref _inttemp, value, "inttemp");
        }
    }
	private int _errs;
	public int errs
	{
		get
		{
			return _errs;
		}
		set
		{
			SetField(ref _errs, value, "errs");
		}
	}
}

public class Dat
{
    private byte[] buf = null;
    private int[] ms = null;
    private int cur = 0;
    private DateTime st;
    private bool saved = false, go = false;
    private string fileName;
    private FileStream fs = null;
    public bool pause = false;
    private void setFn(string fn)
    {
		fileName = Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE"), "Downloads", fn);
	}
    public Dat(string fn)
    {
        setFn(fn);
		load();
	}
	public Dat(int size, string fn)
    {
        setFn(fn);
		buf = new byte[size];
		ms = new int[size];
	}
    public int CaptPos() => cur;
    public void add(byte b)
    {
        if (cur == 0)
            st = DateTime.Now;
        if (cur == buf.Length)
        {
            if (saved) return;
			Thread s = new Thread(saver);
			s.Start();
			saved = true;
			return;
        }
        buf[cur] = b;
        ms[cur++] = (int)(DateTime.Now - st).TotalMilliseconds;
    }
    public int read(byte[] outbuf, int offset, int count)
    {
        if ((pause && !go) || cur == buf.Length)
        {
            DateTime x = DateTime.Now;
            Thread.Sleep(1000);
            st += DateTime.Now - x;
            return 0;
        }
        if (cur == 0)
            st = DateTime.Now;
        if (count > (buf.Length - cur))
            count = buf.Length - cur;
        System.Buffer.BlockCopy(buf, cur, outbuf, offset, count);
        var diff = DateTime.Now - st;
		int mils = ms[cur] - (int)(diff.TotalMilliseconds);
        cur += count;
        if (mils > 0)
            Thread.Sleep(mils);

        return count;
    }

    public void load()
    {
        FileInfo fi = new FileInfo(fileName);
        int elements = (int)(fi.Length / (sizeof(byte) + sizeof(int)));
        fs = File.Open(fileName, FileMode.Open);
        buf = new byte[elements];
        ms = new int[elements];

        fs.Read(buf, 0, elements);
        byte[] bytes = new byte[buf.Length * sizeof(int)];
        fs.Read(bytes, 0, elements * sizeof(int));
        fs.Close();
        System.Buffer.BlockCopy(bytes, 0, ms, 0, bytes.Length);
    }
    private void saver()
    {
		FileStream fs = File.Open(fileName, FileMode.Create);
		fs.Write(buf, 0, buf.Length);
		byte[] bytes = new byte[buf.Length * sizeof(int)];
		System.Buffer.BlockCopy(ms, 0, bytes, 0, bytes.Length);

		fs.Write(bytes, 0, bytes.Length);
		fs.Close();
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
