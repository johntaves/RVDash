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
    private Dat d=new Dat(@"c:\users\hp\Downloads\binx.dat");
    public int lastTime=0;
    public SerRead()
    {
        if (!d.sim)
        {
            sp = new SerialPort();
            sp.PortName = "COM8";
            sp.BaudRate = 9600;
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
    private SerRead serialPort = new SerRead();
    public ObservableCollection<Msg> MsgList = new();
    private Queue<Msg> queue = new Queue<Msg>();
    public static int OOWCnt = 0;
    private bool done = false;
    public MainWindow()
    {
        InitializeComponent();
        this.Loaded += new RoutedEventHandler(Window_Loaded);
        this.Closed += Window_Closed;
        Thread t = new Thread(readLoop);
        t.Start();
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
    private void readLoop()
    {
        byte sum = 0;
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
            if (false)
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
            if (m.pid == 190 && (int)m.value > 0)
                serialPort.pause();
            if (m.mid == 140)
            {
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
            if (ID2Str.MIDs.ContainsKey(mid)) return ID2Str.MIDs[mid];
            return mid.ToString();
        }
    }
    public string PID
    {
        get
        {
            if (ID2Str.PIDs.ContainsKey(pid)) return ID2Str.PIDs[rPid];
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
    public Dat()
    {
    }
    public Dat(string fn)
    {
        fileName = fn;
        load();
        sim = true;
    }
    public Dat(int size, string fn)
    {
        capt = true;
        buf = new byte[size];
        ms = new int[size];
        fileName = fn;
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
        if (true)
            Interpret();
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
    public void Interpret()
    {
        StreamWriter w = new StreamWriter(@"c:\users\hp\Downloads\foo.txt");
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
                w.Write(string.Format("{0}: {1} {2}:", ms[pos].ToString("D6"), buf[pos++], buf[pos++]));
                for (int j = pos; j < i; j++)
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
public class ID2Str
{
    public static IReadOnlyDictionary<int, string> MIDs =>
    new Dictionary<int, string>() {
{128,"Engine #1"}
,{129,"Turbocharger"}
,{130,"Transmission"}
,{131,"Power Takeoff"}
,{132,"Axle, Power Unit"}
,{133,"Axle, Trailer #1"}
,{134,"Axle, Trailer #2"}
,{135,"Axle, Trailer #3"}
,{136,"Brakes, Power Unit"}
,{137,"Brakes, Trailer #1"}
,{138,"Brakes, Trailer #2"}
,{139,"Brakes, Trailer #3"}
,{140,"Instrument Cluster"}
,{141,"Trip Recorder"}
,{142,"Vehicle Management System"}
,{143,"Fuel System"}
,{144,"Cruise Control"}
,{145,"Road Speed Indicator"}
,{146,"Cab Climate Control"}
,{147,"Cargo Refrigeration/Heating, Trailer #1"}
,{148,"Cargo Refrigeration/Heating, Trailer #2"}
,{149,"Cargo Refrigeration/Heating, Trailer #3"}
,{150,"Suspension, Power Unit"}
,{151,"Suspension, Trailer #1"}
,{152,"Suspension, Trailer #2"}
,{153,"Suspension, Trailer #3"}
,{154,"Diagnostic Systems, Power Unit"}
,{155,"Diagnostic Systems, Trailer #1"}
,{156,"Diagnostic Systems, Trailer #2"}
,{157,"Diagnostic Systems, Trailer #3"}
,{158,"Electrical Charging System"}
,{159,"Proximity Detector, Front"}
,{160,"Proximity Detector, Rear"}
,{161,"Aerodynamic Control Unit"}
,{162,"Vehicle Navigation Unit"}
,{163,"Vehicle Security"}
,{164,"Multiplex"}
,{165,"Communication Unit—Ground"}
,{166,"Tires, Power Unit"}
,{167,"Tires, Trailer #1"}
,{168,"Tires, Trailer #2"}
,{169,"Tires, Trailer #3"}
,{170,"Electrical"}
,{171,"Driver Information Center"}
,{172,"Off-board Diagnostics #1"}
,{173,"Engine Retarder"}
,{174,"Cranking/Starting System"}
,{175,"Engine #2"}
,{176,"Transmission, Additional"}
,{177,"Particulate Trap System"}
,{178,"Vehicle Sensors to Data Converter"}
,{179,"Data Logging Computer"}
,{180,"Off-board Diagnostics #2"}
,{181,"Communication Unit—Satellite"}
,{182,"Off-board Programming Station"}
,{183,"engine #3"}
,{184,"Engine #4"}
,{185,"Engine #5"}
,{186,"Engine #6"}
,{187,"Vehicle Control Head Unit/Vehicle Management System #2"}
,{188,"Vehicle Logic Control Unit/Vehicle Management System #3"}
,{189,"Vehicle Head Signs"}
,{190,"Refrigerant Management Protection and Diagnostics"}
,{191,"Vehicle Location Unit—Differential Correction"}
,{192,"Front Door Status Unit"}
,{193,"Middle Door Status Unit"}
,{194,"Rear Door Status Unit"}
,{195,"Annunciator Unit"}
,{196,"Fare Collection Unit"}
,{197,"Passenger Counter Unit #1"}
,{198,"Schedule Adherence Unit"}
,{199,"Route Adherence Unit"}
,{200,"Environment Monitor Unit/Auxiliary Cab Climate Control"}
,{201,"Vehicle Status Points Monitor Unit"}
,{202,"High Speed Communications Unit"}
,{203,"Mobile Data Terminal Unit"}
,{204,"Vehicle Proximity, Right Side"}
,{205,"Vehicle Proximity, Left Side"}
,{206,"Base Unit (Radio Gateway to Fixed End)"}
,{207,"Bridge from SAE J1708 Drivetrain Link"}
,{208,"Maintenance Printer"}
,{209,"Vehicle Turntable"}
,{210,"Bus Chassis Identification Unit"}
,{211,"Smart Card Terminal"}
,{212,"Mobile Data Terminal"}
,{213,"Vehicle Control Head Touch Screen"}
,{214,"Silent Alarm Unit"}
,{215,"Surveillance Microphone"}
,{216,"Lighting Control Administrator Unit"}
,{217,"Tractor/Trailer Bridge, Tractor Mounted"}
,{218,"Tractor/Trailer Bridge, Trailer Mounted"}
,{219,"Collision Avoidance Systems"}
,{220,"Tachograph"}
,{221,"Driver Information Center #2"}
,{222,"Driveline Retarder"}
,{223,"Transmission Shift Console—Primary"}
,{224,"Parking Heater"}
,{225,"Weighing System, Axle Group #1/Vehicle"}
,{226,"Weighing System, Axle Group #2"}
,{227,"Weighing System, Axle Group #3"}
,{228,"Weighing System, Axle Group #4"}
,{229,"Weighing System, Axle Group #5"}
,{230,"Weighing System, Axle Group #6"}
,{231,"Communication Unit—Cellular"}
,{232,"Safety Restraint System"}
,{233,"Intersection Preemption Emitter"}
,{234,"Instrument Cluster #2"}
,{235,"Engine Oil Control System"}
,{236,"Entry Assist Control #1"}
,{237,"Entry Assist Control #2"}
,{238,"Idle Adjust System"}
,{239,"Passenger Counter Unit #2"}
,{240,"Passenger Counter Unit #3"}
,{241,"Fuel Tank Monitor"}
,{242,"Axles, Trailer #4"}
,{243,"Axles, Trailer #5"}
,{244,"Diagnostic Systems, Trailer #4"}
,{245,"Diagnostic Systems, Trailer #5"}
,{246,"Brakes, Trailer #4"}
,{247,"Brakes, Trailer #5"}
,{248,"Forward Road Image Processor"}
,{249,"Body Controller"}
,{250,"Steering Column Unit"}
    };
    public static IReadOnlyDictionary<int, string> PIDs =>
   new Dictionary<int, string>() {
 {0  ,"Request Parameter"}
,{1  ,"Invalid Data Parameter (see Appendix A)"}
,{2  ,"Transmitter System Status (see Appendix A)"}
,{3  ,"Transmitter System Diagnostic (see Appendix A)"}
,{4  ,"Reserved—to be assigned"}
,{5  ,"Underrange Warning Condition (see Appendix A)"}
,{6  ,"Overrange Warning Condition (see Appendix A)"}
,{7  ,"Axle #2 Lift Air Pressure"}
,{8  ,"Brake System Air Pressure Low Warning Switch Status"}
,{9  ,"Axle Lift Status"}
,{10 ,"Axle Slider Status"}
,{11 ,"Cargo Securement"}
,{12 ,"Brake Stroke Status"}
,{13 ,"Entry Assist Position/Deployment"}
,{14 ,"Entry Assist Motor Current"}
,{15 ,"Fuel Supply Pump Inlet Pressure"}
,{16 ,"Suction Side Fuel Filter Differential Pressure"}
,{17 ,"Engine Oil Level Remote Reservoir"}
,{18 ,"Extended Range Fuel Pressure"}
,{19 ,"Extended Range Engine Oil Pressure"}
,{20 ,"Extended Range Engine Coolant Pressure"}
,{21 ,"Engine ECU Temperature"}
,{22 ,"Extended Engine Crankcase Blow-by Pressure"}
,{23 ,"Generator Oil Pressure"}
,{24 ,"Generator Coolant Temperature"}
,{25 ,"Air Conditioner System Status #2"}
,{26 ,"Estimated Percent Fan Speed"}
,{27 ,"Percent Exhaust Gas Recirculation Valve #1 Position"}
,{28 ,"Percent Accelerator Position #3"}
,{29 ,"Percent Accelerator Position #2"}
,{30 ,"Crankcase Blow-by Pressure"}
,{31 ,"Transmission Range Position"}
,{32 ,"Transmission Splitter Position"}
,{33 ,"Clutch Cylinder Position"}
,{34 ,"Clutch Cylinder Actuator Status"}
,{35 ,"Shift Finger Actuator Status #2"}
,{36 ,"Clutch Plates Wear Condition"}
,{37 ,"Transmission Tank Air Pressure"}
,{38 ,"Second Fuel Level (Right Side)"}
,{39 ,"Tire Pressure Check Interval"}
,{40 ,"Engine Retarder Switches Status"}
,{41 ,"Cruise Control Switches Status"}
,{42 ,"Pressure Switch Status"}
,{43 ,"Ignition Switch Status"}
,{44 ,"Attention/Warning Indicator Lamps Status"}
,{45 ,"Inlet Air Heater Status"}
,{46 ,"Vehicle Wet Tank Pressure"}
,{47 ,"Retarder Status"}
,{48 ,"Extended Range Barometric Pressure"}
,{49 ,"ABS Control Status"}
,{50 ,"Air Conditioner System Clutch Status/Command #1"}
,{51 ,"Throttle Position"}
,{52 ,"Engine Intercooler Temperature"}
,{53 ,"Transmission Synchronizer Clutch Value"}
,{54 ,"Transmission Synchronizer Brake Value"}
,{55 ,"Shift Finger Positional Status"}
,{56 ,"Transmission Range Switch Status"}
,{57 ,"Transmission Actuator Status #2"}
,{58 ,"Shift Finger Actuator Status"}
,{59 ,"Shift Finger Gear Position"}
,{60 ,"Shift Finger Rail Position"}
,{61 ,"Parking Brake Actuator Status"}
,{62 ,"Retarder Inhibit Status"}
,{63 ,"Transmission Actuator Status #1"}
,{64 ,"Direction Switch Status"}
,{65 ,"Service Brake Switch Status"}
,{66 ,"Vehicle Enabling Component Status"}
,{67 ,"Shift Request Switch Status"}
,{68 ,"Torque Limiting Factor"}
,{69 ,"Two Speed Axle Switch Status"}
,{70 ,"Parking Brake Switch Status"}
,{71 ,"Idle Shutdown Timer Status"}
,{72 ,"Blower Bypass Value Position"}
,{73 ,"Auxiliary Water Pump Pressure"}
,{74 ,"Maximum Road Speed Limit"}
,{75 ,"Steering Axle Temperature"}
,{76 ,"Axle #1 Lift Air Pressure"}
,{77 ,"Forward Rear Drive Axle Temperature"}
,{78 ,"Rear Rear-Drive Axle Temperature"}
,{79 ,"Road Surface Temperature"}
,{80 ,"Washer Fluid Level"}
,{81 ,"Particulate Trap Inlet Pressure"}
,{82 ,"Air Start Pressure"}
,{83 ,"Road Speed Limit Status"}
,{84 ,"Road Speed"}
,{85 ,"Cruise Control Status"}
,{86 ,"Cruise Control Set Speed"}
,{87 ,"Cruise Control High-Set Limit Speed"}
,{88 ,"Cruise Control Low-Set Limit Speed"}
,{89 ,"Power Takeoff Status"}
,{90 ,"PTO Oil Temperature"}
,{91 ,"Percent Accelerator Pedal Position"}
,{92 ,"Percent Engine Load"}
,{93 ,"Output Torque"}
,{94 ,"Fuel Delivery Pressure"}
,{95 ,"Fuel Filter Differential Pressure"}
,{96 ,"Fuel Level"}
,{97 ,"Water in Fuel Indicator"}
,{98 ,"Engine Oil Level"}
,{99 ,"Engine Oil Filter Differential Pressure"}
,{100,"Engine Oil Pressure"}
,{101,"Crankcase Pressure"}
,{102,"Boost Pressure"}
,{103,"Turbo Speed"}
,{104,"Turbo Oil Pressure"}
,{105,"Intake Manifold Temperature"}
,{106,"Air Inlet Pressure"}
,{107,"Air Filter Differential Pressure"}
,{108,"Barometric Pressure"}
,{109,"Coolant Pressure"}
,{110,"Engine Coolant Temperature"}
,{111,"Coolant Level"}
,{112,"Coolant Filter Differential Pressure"}
,{113,"Governor Droop"}
,{114,"Net Battery Current"}
,{115,"Alternator Current"}
,{116,"Brake Application Pressure"}
,{117,"Brake Primary Pressure"}
,{118,"Brake Secondary Pressure"}
,{119,"Hydraulic Retarder Pressure"}
,{120,"Hydraulic Retarder Oil Temperature"}
,{121,"Engine Retarder Status"}
,{122,"Engine Retarder Percent"}
,{123,"Clutch Pressure"}
,{124,"Transmission Oil Level"}
,{125,"Transmission Oil Level High/Low"}
,{126,"Transmission Filter Differential Pressure"}
,{127,"Transmission Oil Pressure"}
,{128,"Component-specific request"}
,{129,"Injector Metering Rail #2 Pressure"}
,{130,"Power Specific Fuel Economy"}
,{131,"Exhaust Back Pressure"}
,{132,"Mass Air Flow"}
,{133,"Average Fuel Rate"}
,{134,"Wheel Speed Sensor Status"}
,{135,"Extended Range Fuel Delivery Pressure (Absolute)"}
,{136,"Auxiliary Vacuum Pressure Reading"}
,{137,"Auxiliary Gage Pressure Reading #1"}
,{138,"Auxiliary Absolute Pressure Reading"}
,{139,"Tire Pressure Control System Channel Functional Mode"}
,{140,"Tire Pressure Control System Solenoid Status"}
,{141,"Trailer #1, Tag #1, or Push Channel #1 Tire Pressure Target"}
,{142,"Drive Channel Tire Pressure Target"}
,{143,"Steer Channel Tire Pressure Target"}
,{144,"Trailer #1, Tag #1, or Push Channel #1 Tire Pressure"}
,{145,"Drive Channel Tire Pressure"}
,{146,"Steer Channel Tire Pressure"}
,{147,"Average Fuel Economy (Natural Gas)"}
,{148,"Instantaneous Fuel Economy (Natural Gas)"}
,{149,"Fuel Mass Flow Rate (Natural Gas)"}
,{150,"PTO Engagement Control Status"}
,{151,"ATC Control Status"}
,{152,"Number of ECU Resets"}
,{153,"Crankcase Pressure"}
,{154,"Auxiliary Input and Output Status #2"}
,{155,"Auxiliary Input and Output Status #1"}
,{156,"Injector Timing Rail Pressure"}
,{157,"Injector Metering Rail Pressure"}
,{158,"Battery Potential (Voltage)—Switched"}
,{159,"Gas Supply Pressure"}
,{160,"Main Shaft Speed"}
,{161,"Input Shaft Speed"}
,{162,"Transmission Range Selected"}
,{163,"Transmission Range Attained"}
,{164,"Injection Control Pressure"}
,{165,"Compass Bearing"}
,{166,"Rated Engine Power"}
,{167,"Alternator Potential (Voltage)"}
,{168,"Battery Potential (Voltage)"}
,{169,"Cargo Ambient Temperature"}
,{170,"Cab Interior Temperature"}
,{171,"Ambient Air Temperature"}
,{172,"Air Inlet Temperature"}
,{173,"Exhaust Gas Temperature"}
,{174,"Fuel Temperature"}
,{175,"Engine Oil Temperature"}
,{176,"Turbo Oil Temperature"}
,{177,"Transmission #1 Oil Temperature"}
,{178,"Front Axle Weight"}
,{179,"Rear Axle Weight"}
,{180,"Trailer Weight"}
,{181,"Cargo Weight"}
,{182,"Trip Fuel"}
,{183,"Fuel Rate (Instantaneous)"}
,{184,"Instantaneous Fuel Economy"}
,{185,"Average Fuel Economy"}
,{186,"Power Takeoff Speed"}
,{187,"Power Takeoff Set Speed"}
,{188,"Idle Engine Speed"}
,{189,"Rated Engine Speed"}
,{190,"Engine Speed"}
,{191,"Transmission Output Shaft Speed"}
,{192,"Multisection Parameter"}
,{193,"Transmitter System Diagnostic Table"}
,{194,"Transmitter System Diagnostic Code and"}
,{195,"Diagnostic Data Request/Clear Count"}
,{196,"Diagnostic Data/Count Clear Response"}
,{197,"Connection Management"}
,{198,"Connection Mode Data Transfer"}
,{199,"Traction Control Disable State"}
,{209,"ABS Control Status, Trailer"}
,{210,"Tire Temperature (By Sequence Number)"}
,{211,"Tire Pressure (By Sequence Number)"}
,{212,"Tire Pressure Target (By Sequence Number)"}
,{213,"Wheel End Assembly Vibration Level"}
,{214,"Vehicle Wheel Speeds"}
,{215,"Brake Temperature"}
,{216,"Wheel Bearing Temperature"}
,{217,"Fuel Tank/Nozzle Identification"}
,{218,"State Line Crossing"}
,{219,"Current State and Country"}
,{220,"Engine Torque History"}
,{221,"Anti-theft Request"}
,{222,"Anti-theft Status"}
,{223,"Auxiliary A/D Counts"}
,{224,"Immobilizer Security Code"}
,{225,"Reserved for Text Message Acknowledged"}
,{226,"Reserved for Text Message to Display"}
,{227,"Reserved for Text Message Display Type"}
,{228,"Speed Sensor Calibration"}
,{229,"Total Fuel Used (Natural Gas)"}
,{230,"Total Idle Fuel Used (Natural Gas)"}
,{231,"Trip Fuel (Natural Gas)"}
,{232,"DGPS Differential Correction"}
,{233,"Unit Number (Power Unit)"}
,{234,"Software Identification"}
,{235,"Total Idle Hours"}
,{236,"Total Idle Fuel Used"}
,{237,"Vehicle Identification Number"}
,{238,"Velocity Vector"}
,{239,"Vehicle Position"}
,{240,"Change Reference Number"}
,{241,"Tire Pressure by Position"}
,{242,"Tire Temperature by Position"}
,{243,"Component Identification"}
,{244,"Trip Distance"}
,{245,"Total Vehicle Distance"}
,{246,"Total Vehicle Hours"}
,{247,"Total Engine Hours"}
,{248,"Total PTO Hours"}
,{249,"Total Engine Revolutions"}
,{250,"Total Fuel Used"}
,{251,"Clock"}
,{252,"Date"}
,{253,"Elapsed Time"}
,{254,"Data Link Escape"}
,{255,"Extension" } };

}
