using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Formats.Tar;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace RVDash;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
/// 
[Serializable]
public class SerRead
{
    private SerialPort sp=null;
	private Dat d;
    byte readPos = 0, writePos = 0;
    byte[] data = new byte[256];
    public SerRead(int port = 0,int size=0, string fn = "")
    {
        d = new Dat(size,fn);
        if (!d.sim)
        {
            sp = new SerialPort();
            sp.PortName = string.Format("COM{0}",port);
            sp.BaudRate = 9600;
            sp.StopBits = StopBits.One;
            sp.DataBits = 8;
            sp.Parity = Parity.None;
            sp.Open();
        }
    }

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
    private void Read()
    {
        int amt;
        if (readPos <= writePos)
        {
            amt = 256 - writePos;
            if (readPos == 0) amt--;
        }
        else amt = readPos - writePos - 1;
        if (amt > 25) amt = 25;
        int len;
        if (sp != null && !d.capt)
            len = sp.Read(data, writePos, amt);
        else if (sp != null)
            len = ReadCapt(data, writePos, amt);
        else len = d.read(data, writePos, amt);
        writePos += (byte)len;
    }
    public byte getNext()
    {
        while (readPos == writePos)
            Read();
        return data[readPos++];
    }
    public byte getNextOOW()
    {
        byte ret;
        while ((ret = getNext()) < 128) ;
        return ret;
    }
}

public partial class MainWindow : Window
{
    protected Gauges gauges = new();
    private Queue<Msg> queue = new Queue<Msg>();
    public static int OOWCnt = 0;
    private bool done = false;
    private MsgListWindow mlw;
    public MainWindow()
    {
        InitializeComponent();
        this.Loaded += new RoutedEventHandler(Window_Loaded);
        this.Closed += Window_Closed;
        Thread tECU = new Thread(readLoop);
        Thread tVDC = new Thread(readLoop);
		mlw = new MsgListWindow();
        mlw.Show();
		tECU.Start(new SerRead(5,0,@"c:\users\john\Downloads\binE.dat"));
	    tVDC.Start(new SerRead(6));
    }
    void Window_Loaded(object sender, RoutedEventArgs e)
    {
        //Set the current value of the gauges
        this.DataContext = gauges;
    }
    void Window_Closed(object sender, System.EventArgs e)
    {
        done = true;
    }
    private void readLoop(object sr)
    {
        SerRead serialPort = (SerRead)sr;
        bool outOfWhack = false;
        Dictionary<string,DateTime> msgs = new Dictionary<string,DateTime>();

        while (!done) // 1 message, many PID in each loop
        {
            byte MID;
            if (outOfWhack)
            {
                OOWCnt++;
                MID = serialPort.getNextOOW();
                outOfWhack = false;
            }
            else MID = serialPort.getNext();
            byte sum = MID;
            object value = 0;
            int packetLen = 0;
            List<Msg> toSend = new List<Msg>();

            while (!outOfWhack)
            {
                byte rPid = serialPort.getNext();
                sum += rPid;
                packetLen++;
                if (sum == 0)
                    break;
                UInt16 pid = (UInt16)rPid;
                if (rPid == 255)
                {
                    rPid = serialPort.getNext();
                    sum += rPid;
                    pid = (UInt16)(rPid + 256);
                    packetLen++;
                }
                if (rPid < 128)
                {
                    byte b = serialPort.getNext();
                    sum += b;
                    value = b;
                    packetLen++;
                }
                else if (rPid < 192)
                {
                    byte b = serialPort.getNext();
                    sum += b;
                    byte c = serialPort.getNext();
                    sum += c;
                    value = (UInt16)(b + (256 * c));
                    packetLen += 2;
                }
                else if (rPid < 254)
                {
                    byte len = serialPort.getNext();
                    sum += len;
                    byte[] buf = new byte[len];
                    value = buf;
                    for (int i = 0; i < len; i++)
                    {
                        buf[i] = serialPort.getNext();
                        sum += buf[i];
                    }
                    packetLen += len;
                }
                else // 254, 510, 511
                    outOfWhack = true;
                if (packetLen > 21)
                    outOfWhack = true;
				toSend.Add(new Msg(MID, pid, value));
            }
            if (!outOfWhack)
                foreach (Msg m in toSend)
                {
                    if (m.mid != 140 && InstPIDs.Contains(m.pid)) continue;
                    if (!msgs.ContainsKey(m.Code) || true || ((DateTime.Now - msgs[m.Code]).Milliseconds > 50))
                    {
                        msgs[m.Code] = DateTime.Now;
                        lock (queue) queue.Enqueue(m);
                        Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => DoUIChange()));
                    }
                }
		}
	}
    private static HashSet<int> InstPIDs =>
        new HashSet<int>()
            { 84,96,100,102,110,117,118,168,177,190,245 };
    private static string[] ILStr = { "Off", "On", "Err", "NA" };
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
            switch (m.pid)
            {
                case 44: int val = Convert.ToInt32(m.value); gauges.idiotlight = string.Format("{0}: Pro {1}, Amb {2}, Red {3}",m.MID, ILStr[(val >> 4) & 3], ILStr[(val >> 2) & 3], ILStr[val & 3]); break;
                case 84: gauges.speed = Convert.ToInt32(m.value) / 2; break;
                case 96: gauges.fuel = Convert.ToInt32(m.value) / 2; break;
                case 100: gauges.oil = Convert.ToInt32(m.value) / 2; break;
                case 102: gauges.boost = Convert.ToInt32(m.value) / 8; break;
                case 110: gauges.water = Convert.ToInt32(m.value); break;
                case 117: gauges.airPrim = Convert.ToInt32(m.value) * 3 / 5; break;
                case 118: gauges.airSec = Convert.ToInt32(m.value) * 3 / 5; break;
                //2 byte
                case 168: gauges.volts = (Convert.ToDecimal(m.value) * .05M).ToString("F1"); break;
                case 177: gauges.transTemp = Convert.ToInt16(m.value) / 4; break;
                case 190: gauges.rpm = Convert.ToInt32(m.value) / 4; break;
                //4 byte:
                case 245: gauges.miles = (BitConverter.ToInt32((byte[])m.value) * .1M).ToString("F1"); break;
                default:
                    mlw.AddToList(m);
					break;
            }
        }
    }
}
public class Msg
{
    public byte mid;
    public UInt16 pid;
    public object value;
    public int cnt=1;
    public Msg(byte m, UInt16 p, object v)
    {
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
    private int _rpm;
    public int rpm
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
    public bool sim = false, capt = false, pause = false;
    public Dat(int size = 0, string fn = "")
    {
		fileName = fn;
		if (size > 0) {
			buf = new byte[size];
			ms = new int[size];
			capt = true;
			if (fn.Length == 0)
                throw new Exception("Bullshit");
		} else if (fn.Length > 0)
        {
            sim = true;
			load();
		}
	}
    public void add(byte b)
    {
        if (cur == 0)
            st = DateTime.Now;
        if (cur == buf.Length)
        {
            save();
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
        int mils = ms[cur] - (int)((DateTime.Now - st).TotalMilliseconds);
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
    public void save()
    {
        if (saved) return;
        FileStream fs = File.Open(fileName, FileMode.Create);
        fs.Write(buf, 0, buf.Length);
        byte[] bytes = new byte[buf.Length * sizeof(int)];
        System.Buffer.BlockCopy(ms, 0, bytes, 0, bytes.Length);

        fs.Write(bytes, 0, bytes.Length);
        fs.Close();
        saved = true;
	    return;
    }
}
