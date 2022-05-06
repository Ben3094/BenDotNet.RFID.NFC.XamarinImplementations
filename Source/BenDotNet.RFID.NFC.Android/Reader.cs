using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading.Tasks;

using BenDotNet.RFID.NFC.NFC_A;

using Xamarin.Essentials;

using Android.App;
using AndroidNFC = Android.Nfc;
using Android.Nfc.Tech;
using Android.Content;
using Android.Nfc;

namespace BenDotNet.RFID.NFC.Android
{
    public class AntennaPort : RFID.AntennaPort
    {
        public AntennaPort(Reader reader) : base(reader) { }
    }

    public class Reader : NFC.Reader
    {
        private Activity attachedActivity;
        private Activity associatedActivity = null;
        public Activity AssociatedActivity
        {
            get => this.associatedActivity ?? Platform.CurrentActivity;
            set => this.associatedActivity = value;
        }

        public AndroidNFC.NfcAdapter NFCAdapter;
        private AndroidNFCReaderCallback androidNFCReaderCallback;
        private PendingIntent pendingIntent;
        private static IntentFilter[] intentFiltersArray = new IntentFilter[] { new IntentFilter(NfcAdapter.ActionTechDiscovered) };

        private IReadOnlyList<AntennaPort> antennaPorts = new List<AntennaPort>().AsReadOnly();
        public override IEnumerable<RFID.AntennaPort> AntennaPorts => this.antennaPorts;

        public override float Frequency { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
        public override float Power { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

        public override float MaxAllowedPower => throw new NotImplementedException();

        #region Instantiation
        public Reader()
        {
            this.constructor();
        }
        public Reader(Activity activity, AndroidNFC.NfcAdapter nfcAdapter)
        {
            this.constructor(activity, nfcAdapter);
        }
        public void constructor(Activity activity = null, AndroidNFC.NfcAdapter nfcAdapter = null)
        {
            if (activity == null)
                activity = Platform.CurrentActivity;
            this.AssociatedActivity = activity;

            if (nfcAdapter == null)
                nfcAdapter = AndroidNFC.NfcAdapter.GetDefaultAdapter(this.AssociatedActivity);
            this.NFCAdapter = nfcAdapter;

            //pendingIntent = PendingIntent.GetActivity((Context)this.AssociatedActivity, 0, new Intent(this.AssociatedActivity, this.AssociatedActivity.GetType()).AddFlags(ActivityFlags.SingleTop), PendingIntentFlags.Immutable);
            //this.NFCAdapter.EnableForegroundDispatch(this.AssociatedActivity, pendingIntent, intentFiltersArray, null);

            this.antennaPorts = new List<AntennaPort>() { new AntennaPort(this) }.AsReadOnly();

            this.AutoInventoryTimer = new System.Timers.Timer() { Interval = 10000 };

            this.androidNFCReaderCallback = new AndroidNFCReaderCallback(this);
        }
        public void Dispose() { }
        #endregion

        #region Inventory
        internal ConcurrentQueue<RFID.Tag> tagInventoryBuffer = new ConcurrentQueue<RFID.Tag>();

        internal bool isInventorying = false;
        //private void AutoInventoryTimer_Tick(object sender, ElapsedEventArgs e)
        //{
        //    this.NFCAdapter.EnableReaderMode(this.AttachedActivity, this.androidNFCReaderCallback, NfcReaderFlags.NfcA | NfcReaderFlags.NfcB | NfcReaderFlags.NfcF | NfcReaderFlags.NfcV | NfcReaderFlags.NfcBarcode, null);
        //    Task.Delay(this.autoInventoryDelay).Wait();
        //    this.NFCAdapter.DisableReaderMode(this.AttachedActivity);
        //}
        public override void StartContinuousInventory(TimeSpan interval, TimeSpan delay)
        {
            this.attachedActivity = this.AssociatedActivity;
            this.NFCAdapter.EnableReaderMode(this.AssociatedActivity, this.androidNFCReaderCallback, AndroidNFC.NfcReaderFlags.NfcA | AndroidNFC.NfcReaderFlags.NfcB | AndroidNFC.NfcReaderFlags.NfcF | AndroidNFC.NfcReaderFlags.NfcV | AndroidNFC.NfcReaderFlags.NfcBarcode, null); ;

            //this.autoInventoryDelay = delay;
            //this.AutoInventoryTimer.Elapsed += AutoInventoryTimer_Tick;
            //this.AutoInventoryTimer.Interval = interval.TotalMilliseconds;
            //this.AutoInventoryTimer.Start();
        }
        public override void StopContinuousInventory()
        {
            this.NFCAdapter.DisableReaderMode(this.attachedActivity);
        }

        public override IEnumerable<RFID.Tag> Inventory(TimeSpan delay)
        {
            this.isInventorying = true;
            this.StartContinuousInventory();
            Task.Delay(delay).Wait();
            this.StopContinuousInventory();
            this.isInventorying = false;

            RFID.Tag detectedTag;
            while (this.tagInventoryBuffer.Count > 0)
            {
                if (this.tagInventoryBuffer.TryDequeue(out detectedTag))
                    yield return detectedTag;
            }
        }
        #endregion

        public override RFID.Reply Execute(RFID.Tag targetTag, RFID.Command command)
        {
            if (!targetTag.GetType().IsSubclassOf(typeof(Tag)))
                throw new ArgumentException("Only NFC tag allowed");
            if (!command.GetType().IsSubclassOf(typeof(Command)))
                throw new ArgumentException("Only NFC command allowed");

            RFID.Tag tag = targetTag;
            Tag nfcTag = (Tag)targetTag;
            Command nfcCommand = (Command)command;

            //if (this.Detect(ref tag) == null)
            //    throw new InvalidOperationException("Tag not detected");

            BasicTagTechnology androidNFCTag = (BasicTagTechnology)nfcTag.DetectionSources.First(detectionSource => detectionSource.Antenna == this.AntennaPorts.First()).Handle;

            byte[] receivedData = new byte[] { };
            androidNFCTag.Connect();
            try
            {
                switch (androidNFCTag)
                {
                    case NfcA nfcA:
                        receivedData = nfcA.Transceive(nfcCommand.BytesCompiledCommand);
                        break;
                    case NfcV nfcV:
                        receivedData = nfcV.Transceive(nfcCommand.BytesCompiledCommand);
                        break;

                    default:
                        throw new NotSupportedException("NFC tag technology not supported");
                }
            }
            finally { androidNFCTag.Close(); } //Absolutely need to be called even if the sending failed

            Type replyType = ((ReplyTypeAttribute)nfcCommand.GetType().GetCustomAttributes(typeof(ReplyTypeAttribute), false).First()).PossibleReplyTypes;
            return (Reply)Activator.CreateInstance(replyType, nfcCommand, receivedData);
        }

        internal class AndroidNFCReaderCallback : Java.Lang.Object, AndroidNFC.NfcAdapter.IReaderCallback
        {
            private readonly Reader reader;

            public AndroidNFCReaderCallback(Reader reader) { this.reader = reader; }

            public void OnTagDiscovered(AndroidNFC.Tag discoveredTag)
            {
                try
                {
                    object tag;
                    Type tagType = null;
                    byte[] UID = null;
                    RFID.AntennaPort mainAntennaPort = this.reader.AntennaPorts.First();

                    switch (discoveredTag.GetTechList()[0])
                    {
                        case "android.nfc.tech.NfcA":
                            tag = NfcA.Get(discoveredTag);
                            RFID.Command dummyCommand = null;
                            if (new AnswerToRequestTypeAReply(ref dummyCommand, ((NfcA)tag).GetAtqa().Reverse().ToArray()).IsType1Tag)
                                tagType = typeof(NFC_A.Topaz.Tag);
                            else
                                tagType = typeof(NFC_A.Mifare.Tag);
                            break;

                        case "android.nfc.tech.NfcV":
                            tag = NfcV.Get(discoveredTag);
                            tagType = typeof(NFC_V.Tag);
                            break;

                        default:
                            tag = discoveredTag;
                            tagType = typeof(Tag);
                            break;
                    }

                    RFID.Tag detectedTag = GlobalTagCache.NotifyDetection(UID = discoveredTag.GetId(), tagType, new DetectionSource(ref mainAntennaPort, ref tag, float.NaN, float.NaN, float.NaN, DateTime.Now));
                    if (this.reader.isInventorying == true)
                        this.reader.tagInventoryBuffer.Enqueue(detectedTag);
                }
                catch (Exception ex) { }
            }
        }
    }
}