using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Formats.Tar;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Runtime.Serialization.Formatters.Binary;
using System.Text;
using System.Threading;
using System.Windows;
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
	public int lastTime=0;
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
        lastTime = d.lastTime;
        return ret;
    } 
    public int Read(byte[] buf, int offset, int count)
    {
        if (sp != null && !d.capt)
            return sp.Read(buf, offset, count);
        if (sp != null)
            return ReadCapt(buf, offset, count);
        return d.read(buf, offset, count);
    }
}

public partial class MainWindow : Window
{
    protected Gauges gauges = new();
    public ObservableCollection<Msg> MsgList = new();
    private Queue<Msg> queue = new Queue<Msg>();
    public static int OOWCnt = 0;
    private bool done = false;
    public MainWindow()
    {
        InitializeComponent();
        this.Loaded += new RoutedEventHandler(Window_Loaded);
        this.Closed += Window_Closed;
        Thread tECU = new Thread(readLoop);
        Thread tVDC = new Thread(readLoop);

		//tECU.Start(new SerRead(5,5000,@"c:\users\john\Downloads\bin.dat"));
	    tVDC.Start(new SerRead(6));
    }
    void Window_Loaded(object sender, RoutedEventArgs e)
    {
        //Set the current value of the gauges
        this.DataContext = gauges;
        this.lstCodes.ItemsSource = MsgList;
    }
    void Window_Closed(object sender, System.EventArgs e)
    {
        done = true;
    }
    private void readLoop(object sr)
    {
        byte sum = 0;
        SerRead serialPort = (SerRead)sr;
        byte readPos=0, writePos = 0;
        bool outOfWhack = false;
        var data = new byte[256];
        Dictionary<string,DateTime> msgs = new Dictionary<string,DateTime>();

        while (!done)
        {
            int amt;
            if (readPos <= writePos)
            {
                amt = 256 - writePos;
                if (readPos == 0) amt--;
            }
            else amt = readPos - writePos - 1;
            if (amt > 25) amt = 25;
            var len = serialPort.Read(data, writePos, amt);
            writePos += (byte)len;

            if (outOfWhack)
                OOWCnt++;
            while (outOfWhack && readPos != writePos)
            {
                if (data[readPos] == 140)
                    outOfWhack = false;
                else
                    readPos += 1;
            }
            while (!outOfWhack && (byte)(writePos-readPos) > 3)
            {
                Msg m = new();
                if (!m.set(data,ref readPos, writePos,ref outOfWhack) || outOfWhack)
                    break;
                m.time = serialPort.lastTime;
                if (!msgs.ContainsKey(m.Code) || true || ((DateTime.Now - msgs[m.Code]).Milliseconds > 50))
                {
                    msgs[m.Code] = DateTime.Now;
                    lock (queue) queue.Enqueue(m);
                    Dispatcher.BeginInvoke(DispatcherPriority.Normal, new Action(() => DoUIChange()));
                }
            }
        }
    }
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
            var i = MsgList.IndexOf(m);
            if (true)
            {
                if (i >= 0)
                {
                    m.cnt = MsgList[i].cnt + 1;
                    MsgList.RemoveAt(i);
                    MsgList.Insert(i, m);
                }
                else
                {
                    for (i = 0; i < MsgList.Count && MsgList[i].LessThan(m); i++) ;
                    MsgList.Insert(i, m);
                }
            }
            else if (m.pid == 226)
                MsgList.Add(m);
            gauges.errs = OOWCnt;
            switch (m.pid)
            {
                case 8: break; // air under warning 
                case 45: break; // inlet air status
                case 76: break; // axle 1 lift air
                case 84: gauges.speed = Convert.ToInt32(m.value) / 2; break;
                case 85: break; // cruise on/off bit 8
                case 86: break; // cruise speed /2
                case 96: gauges.fuel = Convert.ToInt32(m.value) / 2; break;
                case 100: gauges.oil = Convert.ToInt32(m.value) / 2; break;
                case 102: gauges.boost = Convert.ToInt32(m.value) / 8; break;
                case 110: gauges.water = Convert.ToInt32(m.value); break;
                case 117: gauges.airPrim = Convert.ToInt32(m.value) * 3 / 5; break;
                case 118: gauges.airSec = Convert.ToInt32(m.value) * 3 / 5; break;
                //2 byte
                case 168: gauges.volts = (Convert.ToDecimal(m.value) * .05M).ToString("F1"); break;
                case 177: gauges.transTemp = Convert.ToInt16(m.value) / 4; break;
                case 190: gauges.rpm = (int)m.value / 4; break;
                //4 byte:
                case 245: gauges.miles = (BitConverter.ToInt32((byte[])m.value) * .1M).ToString("F1"); break;
                default:
                    break;
            }
        }
    }
}
public class Msg
{
    public byte mid;
    private byte len, rPid;
    public UInt16 pid;
    public object value;
    public byte[] rawData;
    public int cnt=1;
    public int time;
    public Msg()
    {
    }
    private void getBytes(byte[] data, byte rp)
    {
        int l = len+3;
        if (pid > 255) l++;
        rawData = new byte[l];
        for (int i=0;i<l; i++)
            rawData[i] = data[rp++];
    }
    public bool set(byte[] data, ref byte readPos, byte writePos, ref bool outOfWhack) // returns false if not enough data
    {
        byte rp = readPos;
        byte remLen = (byte)(writePos - readPos);
        if (remLen < 4)
            return false;
        mid = data[rp++];
        rPid = data[rp++];
        byte sum = (byte)(mid + rPid);
        pid = (UInt16)rPid;
        if (mid < 100)
            mid = mid;
        if (rPid == 255)
        {
            remLen--;
            if (remLen < 4)
                return false;
            pid += 256;
            sum += data[rp++];
        }
        if (rPid < 128)
        {
            len = 1;
            value = data[rp];
            sum += data[rp++];
        } else if (rPid < 192)
        {
            if (remLen < 6)
                return false;
            len = 2;
            value = (UInt16)data[rp] + 256 * data[(byte)(rp + 1)];
            sum += data[rp++];
            sum += data[rp++];
        } else if (rPid < 254)
        {
            len = data[rp];
            sum += data[rp++];
            if (remLen < (len + 4))
                return false;
            byte[] buf = new byte[len];
            value = buf;
            for (int i = 0; i < len; i++)
            {
                buf[i] = data[rp];
                sum += data[rp++];
            }
        }
        else // 254, 510, 511
        {
            outOfWhack = true;
            return true;
        }
        sum += data[rp++];
        getBytes(data, readPos);
        readPos = rp;
        outOfWhack = sum != 0;
        return true;
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
    public string Cnt => cnt.ToString();
    public string Code => string.Format("{0} {1}:{2}", mid, pid, len);
    public string Time => time.ToString();
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
            if (IDMaps.PIDs.ContainsKey(pid)) return IDMaps.PIDs[rPid];
            return pid.ToString();
        }
    }
    public static string Str(byte[] a) => String.Join(',', a);
    public string RawData => Str(rawData);
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
    public int lastTime;
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
        lastTime = ms[cur];
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
		Interpret();
	    return;
    }
    public void Interpret()
    {

        StreamWriter w = new StreamWriter(Path.ChangeExtension(fileName,"txt"));
        bool oo = false;
        int pos;
        for (pos = 0; pos < buf.Length - 30;)
        {
            byte mid, rPid;
            byte b = buf[pos];
            int i = pos;
            int len;
            object value;
            byte[] data;
            UInt16 pid;
            if (b < 128 && oo && pos < (buf.Length - 20))
            {
                w.Write(b.ToString() + ",");
                pos++;
                continue;
            }
            len = 0;
            data = null;
            mid = buf[i++];
            rPid = buf[i++];
            byte sum = (byte)(mid + rPid);
            pid = (UInt16)rPid;
            if (rPid == 255)
            {
                pid += 256;
                sum += buf[i++];
            }
            if (rPid < 128)
            {
                len = 1;
                value = buf[i];
                sum += buf[i++];
            }
            else if (rPid < 192)
            {
                len = 2;
                value = (UInt16)buf[i] + 256 * buf[(byte)(i + 1)];
                sum += buf[i++];
                sum += buf[i++];
            }
            else if (rPid < 254)
            {
                len = buf[i];
                if (len > 20)
                {
                    pos++;
                    continue;
                }
                data = new byte[len];
                sum += buf[i++];
                value = data;
                for (int j = 0; j < len; j++)
                {
                    data[j] = buf[i];
                    sum += buf[i++];
                }
            }
            else // 254, 510, 511
            {
            }
            sum += buf[i++];
            if (sum == 0)
            {
                if (oo) w.WriteLine();
                w.Write(string.Format("{0}: {1} {2}:", ms[pos].ToString("D6"), buf[pos], buf[pos+1]));
                for (int j = pos+2; j < i; j++)
                    w.Write(string.Format("{0},", buf[j]));
                if (data != null && len > 4)
                    for (int j = pos + 3; j < i; j++)
                        w.Write(string.Format("{0} ", Convert.ToString(buf[j], 2).PadLeft(8, '0')));
                w.WriteLine();
                pos = i;
            }
            else pos++;
            oo = sum != 0;
        }
        w.Close();
    }
}
