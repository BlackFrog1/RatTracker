﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RatTracker_WPF.Models;
using RatTracker_WPF.Properties;
using SpeechLib;
using WebSocket4Net;
using ErrorEventArgs = SuperSocket.ClientEngine.ErrorEventArgs;

// May be able to drop these.

namespace RatTracker_WPF
{
	/// <summary>
	///     Interaction logic for MainWindow.xaml
	/// </summary>
	public partial class MainWindow : Window
	{
		private static readonly string edsmURL = "http://www.edsm.net/api-v1/";
		private string logDirectory = Settings.Default.NetLogPath;
		private ClientInfo myClient = new ClientInfo();
		private readonly SpVoice voice = new SpVoice();
		private RootObject activeRescues = new RootObject();
		private APIWorker apworker;
		private string currentSystem;
		private long fileOffset;
		private long fileSize;
		private FileInfo logFile;
		private bool onDuty;
		private string parserState = "normal";
		private string scState;
		public bool stopNetLog;
		private Thread threadLogWatcher;
		private FileSystemWatcher watcher;
		private ICollection<TravelLog> myTravelLog;
		static EDSMCoords fuelumCoords = new EDSMCoords() { x = 42, y = -711.09375, z = 39.8125 };
		public MainWindow()
		{
			InitializeComponent();
			CheckLogDirectory();
		}

		public static ConcurrentDictionary<string, Rat> Rats { get; } = new ConcurrentDictionary<string, Rat>();


		private void CheckVerboseLogging()
		{
			/* if (CheckStationLogging())
			{
				appendStatus("Elite Dangerous is not logging system names!!! ");
				appendStatus("Add VerboseLogging=\"1\" to <Network> section in your config file and restart your client!");
			} */
		}

		public void AppendStatus(string text)
		{
			if (StatusDisplay.Dispatcher.CheckAccess())
			{
				StatusDisplay.Text += "\n" + text;
				StatusDisplay.ScrollToEnd();
				StatusDisplay.CaretIndex = StatusDisplay.Text.Length;
			}
			else
			{
				StatusDisplay.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action<string>(AppendStatus),
					text);
			}
		}

		public static byte[] StringToByteArray(string hex)
		{
			return Enumerable.Range(0, hex.Length)
				.Where(x => x%2 == 0)
				.Select(x => Convert.ToByte(hex.Substring(x, 2), 16))
				.ToArray();
		}

		private void OnRenamed(object source, RenamedEventArgs e)
		{
			/* Stop watching the renamed file, look for new onChanged. */
		}

		private void ParseFriendsList(string friendsList)
		{
			/* Sanitize the XML, it can break if over 40 friends long or so. */
			string xmlData;
			int count = 0;
			xmlData = friendsList.Substring(friendsList.IndexOf("<") + friendsList.Length);
			AppendStatus("Raw xmlData: " + xmlData);
			try
			{
				XDocument xdoc = XDocument.Parse(friendsList);
				AppendStatus("Successful XML parse.");
				XElement rettest = xdoc.Element("OK");
				if (rettest != null)
					AppendStatus("Last friendslist action: " + xdoc.Element("OK").Value);
				IEnumerable<XElement> friends = xdoc.Descendants("item");
				foreach (XElement friend in friends)
				{
					byte[] byteenc;
					UnicodeEncoding unicoded = new UnicodeEncoding();
					/* string preencode = Regex.Replace(friend.Element("name").Value, ".{2}", "\\x$0"); */
					byteenc = StringToByteArray(friend.Element("name").Value);
					//appendStatus("Friend:" + System.Text.Encoding.UTF8.GetString(byteenc));
					count++;
					if (friend.Element("pending").Value == "1")
					{
						AppendStatus("Pending invite from CMDR " + Encoding.UTF8.GetString(byteenc) + "detected!");
						Dispatcher disp = Dispatcher;
						Brush frbrush = null;
						voice.Speak("You have a pending friend invite from commander " +
									Encoding.UTF8.GetString(byteenc));
						disp.BeginInvoke(DispatcherPriority.Normal, (Action) (() => { frbrush = FrButton.Background; }));
						if (frbrush != Brushes.Green)
						{
							/* Dear gods, you're a cheap hack, aren't you? */
							disp.BeginInvoke(DispatcherPriority.Normal,
								(Action) (() => FrButton.Background = Brushes.Yellow));
						}
					}
				}

				/* Check the OK status field, which can contain useful information on successful FRs. */
				foreach (XElement element in xdoc.Descendants())
				{
					if (element.Name == "OK")
					{
						AppendStatus("Return code: " + xdoc.Element("data").Element("OK").Value);
						if (xdoc.Element("data").Element("OK").Value.Contains("Invitation accepted"))
						{
							AppendStatus("Friend request accepted!");
							voice.Speak("Friend request accepted.");
							Dispatcher disp = Dispatcher;
							disp.BeginInvoke(DispatcherPriority.Normal,
								(Action) (() => FrButton.Background = Brushes.Green));
						}
					}
				}

				AppendStatus("Parsed " + count + " friends in FRXML.");
			}
			catch (Exception ex)
			{
				AppendStatus("XML Parsing exception:" + ex.Message);
			}
		}

		private void ParseWingInvite(string wingInvite)
		{
			string xmlData;
			xmlData = wingInvite.Substring(wingInvite.IndexOf("<") + wingInvite.Length);
			AppendStatus("Raw xmlData: " + xmlData);
			try
			{
				XDocument xdoc = XDocument.Parse(wingInvite);
				AppendStatus("Successful XML parse.");
				voice.Speak("Wing invite detected.");
				IEnumerable<XElement> wing = xdoc.Descendants("commander");
				foreach (XElement wingdata in wing)
				{
					byte[] byteenc;
					UnicodeEncoding unicoded = new UnicodeEncoding();
					/* string preencode = Regex.Replace(friend.Element("name").Value, ".{2}", "\\x$0"); */
					byteenc = StringToByteArray(wingdata.Element("name").Value);
					AppendStatus("Wingmember:" + Encoding.UTF8.GetString(byteenc));
					if (Encoding.UTF8.GetString(byteenc) == myClient.ClientName)
					{
						AppendStatus("This data matches our current client! Storing information...");
						myClient.ClientId = wingdata.Element("id").Value;
						AppendStatus("Wingmember IP data:" + xdoc.Element("connectionDetails"));
						string wingIPPattern = "IP4NAT:([0-9.]+):\\d+\\,";
						Match wingMatch = Regex.Match(wingInvite, wingIPPattern, RegexOptions.IgnoreCase);
						if (wingMatch.Success)
						{
							AppendStatus("Successful IP data match: " + wingMatch.Groups[1]);
							myClient.ClientIp = wingMatch.Groups[1].Value;
						}

						/* If the friend request matches the client name, store his session ID. */
						myClient.ClientId = wingdata.Element("commander_id").Value;
						myClient.SessionId = wingdata.Element("session_runid").Value;
					}
				}
			}
			catch (Exception ex)
			{
				AppendStatus("Error in parseWingInvite: " + ex.Message);
			}
		}

		private void MainWindow_Closing(object sender, CancelEventArgs e)
		{
			// Clean up our threads and exit.
		}

		private async void CheckLogDirectory()
		{
			if (logDirectory == null | logDirectory == "")
			{
				MessageBox.Show("Error: No log directory is specified, please do so before attempting to go on duty.");
				return;
			}

			if (!Directory.Exists(Settings.Default.NetLogPath))
			{
				MessageBox.Show("Error: Couldn't find E:D Netlog directory: " + Settings.Default.NetLogPath +
								". Please ensure that it is correct in Settings.");
				return;
			}

			StatusDisplay.Text = "Beginning to watch " + logDirectory + " for changes...";
			if (watcher == null)
			{
				watcher = new FileSystemWatcher();
				watcher.Path = logDirectory;
				watcher.NotifyFilter = NotifyFilters.LastAccess | NotifyFilters.LastWrite | NotifyFilters.FileName |
									   NotifyFilters.DirectoryName | NotifyFilters.Size;
				watcher.Filter = "*.log";
				watcher.Changed += OnChanged;
				watcher.Created += OnChanged;
				watcher.Deleted += OnChanged;
				watcher.Renamed += OnRenamed;
				watcher.EnableRaisingEvents = true;
			}

			DirectoryInfo tempDir = new DirectoryInfo(logDirectory);
			logFile = (from f in tempDir.GetFiles("*.log") orderby f.LastWriteTime descending select f).First();
			AppendStatus("Started watching file " + logFile.FullName);
			CheckClientConn(logFile.FullName);
			List<KeyValuePair<string, string>> logindata = new List<KeyValuePair<string, string>>();
			logindata.Add(new KeyValuePair<string, string>("email", "mecha@squeak.net"));
			logindata.Add(new KeyValuePair<string, string>("password", "password"));
			apworker = new APIWorker();
			AppendStatus("Call to APIworker returning :" + apworker.connectAPI());
			object col = await apworker.sendAPI("login", logindata);
			AppendStatus("Login returned: " + col);
			apworker.InitWs();
			apworker.OpenWs();
			ReadLogfile(logFile.FullName);
            apworker.ws.MessageReceived += websocketClient_MessageReceieved;
            myTravelLog = new List<TravelLog>();
		}
        /* Moved WS connection to the apworker, but to actually parse the messages we have to hook the event
         * handler here too.
         */
        private void websocketClient_MessageReceieved(object sender, MessageReceivedEventArgs e)
        {
            dynamic data = JsonConvert.DeserializeObject(e.Message);
            switch ((string)data.type)
            {
                case "welcome":
                    Console.WriteLine("API MOTD: " + data.data);
                    break;
                case "assignment":
                    Console.WriteLine("Got a new assignment datafield: " + data.data);
                    break;
                case "test":
                    /* This is our echo chamber for WS before it actually does anything useful.
                     */
                    AppendStatus("Test data from WS receieved: "+data.data);
                    break;
                default:
                    Console.WriteLine("Unknown API type field: " + data.type + ": " + data.data);
                    break;
            }

        }

        private void ProcessAPIResponse(IAsyncResult result)
		{
			this.AppendStatus("Whaddaya know, ProcessAPIResponse got called!");
		}

		private void CheckClientConn(string lf)
		{
			bool stopSnooping = false;
			AppendStatus("Detecting client connectivity...");
			try
			{
				Dispatcher disp = Dispatcher;
				using (
					StreamReader sr =
						new StreamReader(new FileStream(lf, FileMode.Open, FileAccess.Read,
							FileShare.ReadWrite | FileShare.Delete)))
				{
					int count = 0;
					while (stopSnooping != true && sr.Peek() != -1 && count < 10000)
					{
						count++;
						string line = sr.ReadLine();
						// TODO: Populate WAN, STUN and Turn server labels. Make cleaner TURN detection.
						if (line.Contains("WAN:"))
						{
							AppendStatus("E:D is configured to listen on " + line);
						}

						if (line.Contains("failed to initialise upnp"))
						{
							AppendStatus(
								"CRITICAL: E:D has failed to establish a upnp port mapping, but E:D is configured to use upnp. Disable upnp in netlog if you have manually mapped ports.");
						}

						if (line.Contains("Turn State: Ready"))
						{
							AppendStatus("Client has a valid TURN connection established.");
							disp.BeginInvoke(DispatcherPriority.Normal,
								(Action) (() => ConnTypeLabel.Content = "TURN routed"));
							disp.BeginInvoke(DispatcherPriority.Normal,
								(Action) (() => TurnButton.Background = Brushes.Green));
						}

						if (line.Contains("this machine after STUN reply"))
						{
							AppendStatus("STUN has mapped us to address.");
							disp.BeginInvoke(DispatcherPriority.Normal,
								(Action) (() => ConnTypeLabel.Content = "STUN enabled NAT"));
							disp.BeginInvoke(DispatcherPriority.Normal,
								(Action) (() => StunButton.Background = Brushes.Green));
						}

						if (line.Contains("Sync Established"))
						{
							AppendStatus("Sync Established.");
							disp.BeginInvoke(DispatcherPriority.Normal,
								(Action) (() => SyncButton.Background = Brushes.Green));
						}

						if (line.Contains("ConnectToServerActivity:StartRescueServer"))
						{
							AppendStatus(
								"E:D has established a connection and client is in main menu. Ending early netlog parse.");
							stopSnooping = true;
						}

						if (line.Contains("Symmetrical"))
						{
							AppendStatus(
								"CRITICAL: E:D has detected symmetrical NAT on this connection. This may make it difficult for you to instance with clients!");
							disp.BeginInvoke(DispatcherPriority.Normal,
								(Action) (() => ConnTypeLabel.Content = "Symmetrical NAT"));
							disp.BeginInvoke(DispatcherPriority.Normal,
								(Action) (() => DirectButton.Background = Brushes.Red));
						}
					}

					AppendStatus("Parsed " + count + " lines to derive client info.");
					if (stopSnooping == false)
					{
						AppendStatus(
							"Client connectivity detection complete. You have a direct port mapped address that E:D can use, and should be connectable.");
					}
				}
			}
			catch (Exception ex)
			{
				AppendStatus("Exception in checkClientConn:" + ex.Message);
			}
		}

		private void ReadLogfile(string lf)
		{
			try
			{
				using (
					StreamReader sr =
						new StreamReader(new FileStream(lf, FileMode.Open, FileAccess.Read,
							FileShare.ReadWrite | FileShare.Delete)))
				{
					if (fileOffset == 0L)
					{
						AppendStatus("First peek...");
						if (sr.BaseStream.Length > 5000)
						{
							sr.BaseStream.Seek(-5000, SeekOrigin.End);
							/* First peek into the file, rewind a bit and scan from there. */
						}
					}
					else
					{
						sr.BaseStream.Seek(this.fileOffset, SeekOrigin.Begin);
					}

					while (sr.Peek() != -1)
					{
						string line = sr.ReadLine();
						ParseLine(line);
					}

					//appendStatus("Parsed " + count + " new lines. Old fileOffset was "+fileOffset+" and length was "+logFile.Length);
				}
			}
			catch (Exception ex)
			{
				AppendStatus("Exception in readLogFile: " + ex.Message);
			}
		}

		private void ParseLine(string line)
		{
			/* if (parserState == "ISLAND")
			{
				if (line.Contains(myClient.sessionID.ToString()) && scState=="Normalspace")
				{
					appendStatus("Normalspace Instance match! " + line);
					Dispatcher disp = Dispatcher;
					disp.BeginInvoke(DispatcherPriority.Normal, (Action)(() => wrButton.Background = Brushes.Green));
					//voice.Speak("Successful normal space instance with client.");
				}
			} */
			string reMatchSystem = ".*?(System:).*?\\(((?:[^)]+)).*?\\)";
			Match match = Regex.Match(line, reMatchSystem, RegexOptions.IgnoreCase);
			if (match.Success)
			{
				AppendStatus("System change: " + match.Groups[2].Value + ".");
				TriggerSystemChange(match.Groups[2].Value);
			}

			string reMatchPlayer = "\\{.+\\} (\\d+) x (\\d+).*\\(\\(([0-9.]+):\\d+\\)\\)Name (.+)$";
			Match frmatch = Regex.Match(line, reMatchPlayer, RegexOptions.IgnoreCase);
			if (frmatch.Success)
			{
				AppendStatus("Successful identity match! ID: " + frmatch.Groups[1] + " IP:" + frmatch.Groups[3]);
			}

			if (line.Contains("FriendsRequest"))
			{
				parserState = "xml";
				AppendStatus("Enter XML parse state. Full line: " + line);
				AppendStatus("Received FriendsList update, ready to parse...");
			}

			if (line.Contains("<data>"))
			{
				AppendStatus("Line sent to XML parser");
				ParseFriendsList(line);
			}

			/* Look, we're doing nothing! */
			if (line.Contains("</data>"))
			{
				AppendStatus("Exit XML parsing mode.");
				//parserState = "normal";
			}

			if (line.Contains("<FriendWingInvite>"))
			{
				AppendStatus("Wing invite detected, parsing...");
				ParseWingInvite(line);
				Dispatcher disp = Dispatcher;
				disp.BeginInvoke(DispatcherPriority.Normal, (Action) (() => WrButton.Background = Brushes.Yellow));
			}

			if (line.Contains("JoinSession:WingSession:") && line.Contains(myClient.ClientIp))
			{
				AppendStatus("Prewing communication underway...");
			}

			if (line.Contains("TalkChannelManager::OpenOutgoingChannelTo") && line.Contains(myClient.ClientIp))
			{
				AppendStatus("Wing established, opening voice comms.");
				//voice.Speak("Wing established.");
				Dispatcher disp = Dispatcher;
				disp.BeginInvoke(DispatcherPriority.Normal, (Action) (() => WrButton.Background = Brushes.Green));
			}

			if (line.Contains("ListenResponse->Listening (SUCCESS: User has responded via local talkchannel)"))
			{
				AppendStatus("Voice communications established.");
			}

			if (line.Contains("NormalFlight") && scState == "Supercruise")
			{
				scState = "Normalspace";
				AppendStatus("Drop to normal space detected.");
				//voice.Speak("Dropping to normal space.");
			}

			if (line.Contains("Supercruise") && scState == "Normalspace")
			{
				scState = "Supercruise";
				AppendStatus("Entering supercruise.");
				//voice.Speak("Entering supercruise.");
			}

			if (line.Contains("CLAIMED ------------vvv"))
			{
				AppendStatus("Island claim message detected, parsing members...");
				//parserState = "ISLAND";
			}

			if (line.Contains("claimed ------------^^^"))
			{
				AppendStatus("End of island claim member list. Resuming normal parse.");
				//parserState = "NORMAL";
			}

			if (line.Contains("SESJOINED"))
			{
				AppendStatus("Session join message seen.");
			}

			if (line.Contains("JoinSession:BeaconSession") && line.Contains(myClient.ClientIp))
			{
				AppendStatus("Client's Beacon in sight.");
			}
		}

		private async void TriggerSystemChange(string value)
		{
			Dispatcher disp = Dispatcher;
			if (value == currentSystem)
			{
				return;
			}
			try
			{
				using (HttpClient client = new HttpClient())
				{
					UriBuilder content = new UriBuilder(edsmURL + "systems?sysname=" + value + "&coords=1") {Port = -1};
					NameValueCollection query = HttpUtility.ParseQueryString(content.Query);
					content.Query = query.ToString();
					AppendStatus("Built query string:" + content);
					HttpResponseMessage response = await client.GetAsync(content.ToString());
					response.EnsureSuccessStatusCode();
					string responseString = await response.Content.ReadAsStringAsync();
					AppendStatus("Response string:" + responseString);
					NameValueCollection temp = new NameValueCollection();
					IEnumerable<EDSMSystem> m = JsonConvert.DeserializeObject<IEnumerable<EDSMSystem>>(responseString);
					//voice.Speak("Welcome to " + value);
					EDSMSystem firstsys = m.FirstOrDefault(); // EDSM should return the closest lexical match as the first element. Trust that - for now.
					if (firstsys.name == value)
					{
						if (firstsys.coords == default(EDSMCoords))
							AppendStatus("Got a match on " + firstsys.name + " but it has no coords.");
						else
							AppendStatus("Got definite match in first pos, disregarding extra hits:" + firstsys.name + " X:" + firstsys.coords.x + " Y:" + firstsys.coords.y + " Z:" + firstsys.coords.z);
						//AppendStatus("Got M:" + firstsys.name + " X:" + firstsys.coords.x + " Y:" + firstsys.coords.y + " Z:" + firstsys.coords.z);
						myTravelLog.Add(new TravelLog() { system = firstsys, lastvisited = DateTime.Now }); // Should we add systems even if they don't exist in EDSM? Maybe submit them?
					}
					currentSystem = value;
					await disp.BeginInvoke(DispatcherPriority.Normal, (Action) (() => SystemNameLabel.Content = value));
					if (responseString.Contains("-1"))
					{
						await
							disp.BeginInvoke(DispatcherPriority.Normal,
								(Action) (() => SystemNameLabel.Foreground = Brushes.Red));
					}
					else
					{
						await
							disp.BeginInvoke(DispatcherPriority.Normal,
								(Action) (() => SystemNameLabel.Foreground = Brushes.Yellow));
					}
					if (responseString.Contains("coords"))
					{
						await
							disp.BeginInvoke(DispatcherPriority.Normal,
								(Action) (() => SystemNameLabel.Foreground = Brushes.Green));
                        Console.WriteLine("Getting distance from fuelum to " + firstsys.name);
                        string distance = CalculateEDSMDistance("Fuelum", firstsys.name).ToString();
                        await disp.BeginInvoke(DispatcherPriority.Normal, (Action)(() => distanceLabel.Content = distance + "LY from Fuelum"));
					}
				}
			}
			catch (Exception ex)
			{
				AppendStatus("Exception in triggerSystemChange: " + ex.Message);
			}
		}

		private void OnChanged(object source, FileSystemEventArgs e)
		{
			logFile = new FileInfo(e.FullPath);
			/* Handle changed events */
		}

		private void button_Click(object sender, RoutedEventArgs e)
		{
			if (onDuty == false)
			{
				Button.Content = "On Duty";
				onDuty = true;
				watcher.EnableRaisingEvents = true;
				StatusDisplay.Text += "\nStarted watching for events in netlog.";
				Button.Background = Brushes.Green;
				stopNetLog = false;
				threadLogWatcher = new Thread(NetLogWatcher);
				threadLogWatcher.Name = "Netlog watcher";
				threadLogWatcher.Start();

				ClientName.Text = "Absolver";
				SystemName.Text = "Sagittarius A*";
			}
			else
			{
				Button.Content = "Off Duty";
				onDuty = false;
				watcher.EnableRaisingEvents = false;
				StatusDisplay.Text += "\nStopped watching for events in netlog.";
				Button.Background = Brushes.Red;
				stopNetLog = true;
			}
		}

		private void NetLogWatcher()
		{
			AppendStatus("Netlogwatcher started.");
			bool logChanged = false;
			try
			{
				while (!stopNetLog)
				{
					Thread.Sleep(2000);

					if (logChanged == false)
					{
						FileInfo fi = new FileInfo(logFile.FullName);
						//appendStatus("Netlog tick with status false. LFL:"+fi.Length+ "Filesize: "+fileSize);
						if (fi.Length != fileSize)
						{
							//appendStatus("Log file size increased.");
							ReadLogfile(fi.FullName); /* Maybe a poke on the FS is enough to wake watcher? */
							fileOffset = fi.Length;
							fileSize = fi.Length;
						}
					}
				}
			}
			catch (Exception ex)
			{
				Trace.WriteLine("Netlog exception: " + ex.Message);
			}
		}

		private void Main_Menu_Click(object sender, RoutedEventArgs e)
		{
			/* Fleh? */
		}

		private void frButton_Click(object sender, RoutedEventArgs e)
		{
			if (Equals(FrButton.Background, Brushes.Red))
			{
				FrButton.Background = Brushes.Green;
				AppendStatus("Sending Friend Request acknowledgement.");
				IDictionary<string, string> data = new Dictionary<string, string>();
				data.Add("ReceivedFR", "true");
				apworker.SendWs("FriendRequest", data);
				/* image.Source = new BitmapImage(RatTracker_WPF.Properties.Resources.yellow_light); */
			}
			else
			{
				AppendStatus("Cancelling FR status.");
				FrButton.Background = Brushes.Red;
				IDictionary<string, string> data = new Dictionary<string, string>();
				data.Add("ReceivedFR", "false");
				apworker.SendWs("FriendsRequest", data);
			}
		}

		private void wrButton_Click(object sender, RoutedEventArgs e)
		{
			if (Equals(WrButton.Background, Brushes.Red))
			{
				AppendStatus("Sending Wing Request acknowledgement.");
				IDictionary<string, string> data = new Dictionary<string, string>();
				data.Add("ReceivedWR", "true");
				apworker.SendWs("WingRequest", data);
				WrButton.Background = Brushes.Green;
				/* image.Source = new BitmapImage(RatTracker_WPF.Properties.Resources.yellow_light); */
			}
			else
			{
				AppendStatus("Cancelled WR status.");
				IDictionary<string, string> data = new Dictionary<string, string>();
				data.Add("ReceivedWR", "false");
				apworker.SendWs("WingRequest", data);

				WrButton.Background = Brushes.Red;
			}
		}

		private void bcnButton_Click(object sender, RoutedEventArgs e)
		{
			if (Equals(BcnButton.Background, Brushes.Red))
			{
				AppendStatus("Sending Beacon acknowledgement.");
				BcnButton.Background = Brushes.Green;
				/* image.Source = new BitmapImage(RatTracker_WPF.Properties.Resources.yellow_light); */
			}
			else
			{
				AppendStatus("Cancelling Beacon status.");
				BcnButton.Background = Brushes.Red;
			}
		}

		private void instButton_Click(object sender, RoutedEventArgs e)
		{
			if (Equals(InstButton.Background, Brushes.Red))
			{
				AppendStatus("Sending Good Instance message.");
				InstButton.Background = Brushes.Green;
				/* image.Source = new BitmapImage(RatTracker_WPF.Properties.Resources.yellow_light); */
			}
			else
			{
				AppendStatus("Cancelling good instance message.");
				InstButton.Background = Brushes.Red;
			}
		}

		private void fueledButton_Click(object sender, RoutedEventArgs e)
		{
			if (Equals(FueledButton.Background, Brushes.Red))
			{
				AppendStatus("Reporting fueled status, requesting paperwork link...");
				FueledButton.Background = Brushes.Green;
				/* image.Source = new BitmapImage(RatTracker_WPF.Properties.Resources.yellow_light); */
			}
			else
			{
				AppendStatus("Fueled status now negative.");
				FueledButton.Background = Brushes.Red;
			}
			AppendStatus("Sending fake rescue request!");
			IDictionary<string, string> req = new Dictionary<string, string>();
			req.Add("open", "true");
			//req.Add("_id", myRescue.id); /* TODO: Must hold a handle to my rescue ID somewhere to identify for API interaction */
			apworker.SendWs("rescues", req);
		}

		private void currentButton_Click(object sender, RoutedEventArgs e)
		{
			AppendStatus("Setting client location to current system: Fuelum");
			SystemName.Text = "Fuelum";
		}

		private async void updateButton_Click(object sender, RoutedEventArgs e)
		{
			AppendStatus("Trying to fetch rescues...");
			Dictionary<string, string> data = new Dictionary<string, string>();
			//data.Add("rats", "56a8fcc7abdd7cc91123fd25");
			data.Add("open", "true");
			string col = await apworker.queryAPI("rescues", data);

			if (col == null)
			{
				AppendStatus("No COL returned from Rescues.");
			}
			else
			{
				AppendStatus("Got a COL from Rescues query!");
				RootObject rescues = JsonConvert.DeserializeObject<RootObject>(col);
				await GetMissingRats(rescues);

				RescueGrid.ItemsSource = rescues.Data;
				RescueGrid.AutoGenerateColumns = false;

				foreach (DataGridColumn column in RescueGrid.Columns)
				{
					AppendStatus("Column:" + column.Header);
					if ((string) column.Header == "rats")
					{
						AppendStatus("It's the rats.");
					}
				}
			}
		}

		private async Task GetMissingRats(RootObject rescues)
		{
			IEnumerable<string> ratIdsToGet = new List<string>();

			IEnumerable<List<string>> datas = rescues.Data.Select(d => d.Rats);
			ratIdsToGet = datas.Aggregate(ratIdsToGet, (current, list) => current.Concat(list));
			ratIdsToGet = ratIdsToGet.Distinct().Except(Rats.Values.Select(x => x._Id));

			foreach (string ratId in ratIdsToGet)
			{
				string response =
					await apworker.queryAPI("rats", new Dictionary<string, string> {{"_id", ratId}, {"limit", "1"}});
				JObject jsonRepsonse = JObject.Parse(response);
				List<JToken> tokens = jsonRepsonse["data"].Children().ToList();
				Rat rat = JsonConvert.DeserializeObject<Rat>(tokens[0].ToString());
				Rats.TryAdd(ratId, rat);

				Console.WriteLine("Got name for " + ratId + ": " + rat.CmdrName);
			}
		}

		private void startButton_Click(object sender, RoutedEventArgs e)
		{
			AppendStatus("Started tracking new client " + ClientName.Text);
			myClient.ClientName = ClientName.Text;
			FrButton.Background = Brushes.Red;
			WrButton.Background = Brushes.Red;
			InstButton.Background = Brushes.Red;
			BcnButton.Background = Brushes.Red;
			FueledButton.Background = Brushes.Red;
		}

		private void MenuItem_Click(object sender, RoutedEventArgs e)
		{
			wndSettings swindow = new wndSettings();
			swindow.Show();
		}

		 public IEnumerable<EDSMSystem> QueryEDSMSystem(string system)
		{
			try
			{
				using (HttpClient client = new HttpClient())
				{
					UriBuilder content = new UriBuilder(edsmURL + "systems?sysname=" + system + "&coords=1") { Port = -1 };
					AppendStatus("Querying EDSM for " + system);
					NameValueCollection query = HttpUtility.ParseQueryString(content.Query);
					content.Query = query.ToString();
					HttpResponseMessage response = client.GetAsync(content.ToString()).Result;
					response.EnsureSuccessStatusCode();
					string responseString = response.Content.ReadAsStringAsync().Result;
					//AppendStatus("Got response: " + responseString);
					if (responseString =="-1")
						return new List<EDSMSystem>() { };
					NameValueCollection temp = new NameValueCollection();
					IEnumerable<EDSMSystem> m = JsonConvert.DeserializeObject<IEnumerable<EDSMSystem>>(responseString);
					return m;
				}
			}
			catch (Exception ex)
			{
				AppendStatus("Exception in QueryEDSMSystem: " + ex.Message);
				return new List<EDSMSystem>() { };
			}
		}

		public IEnumerable<EDSMSystem> GetCandidateSystems(string target)
		{
			IEnumerable<EDSMSystem> candidates;
			IEnumerable<EDSMSystem> finalcandidates= new List<EDSMSystem>();
			string sysmatch = "([A-Z][A-Z]-[A-z]+) ([a-zA-Z])+(\\d+(?:-\\d+)+?)";
			Match mymatch = Regex.Match(target, sysmatch,RegexOptions.IgnoreCase);
			candidates = QueryEDSMSystem(target.Substring(0, target.IndexOf(mymatch.Groups[3].Value)));
			AppendStatus("Candidate count is " + candidates.Count().ToString() + " from a subgroup of " + mymatch.Groups[3].Value);
			finalcandidates = candidates.Where(x=>x.coords != null); 
			AppendStatus("FinalCandidates with coords only is size " + finalcandidates.Count());
			if (finalcandidates.Count() < 1)
			{
				AppendStatus("No final candidates, widening search further...");
				candidates = QueryEDSMSystem(target.Substring(0, target.IndexOf(mymatch.Groups[2].Value)));
				finalcandidates = candidates.Where(x=>x.coords!= null);
				if (finalcandidates.Count() < 1)
				{
					AppendStatus("Still nothing! Querying whole sector.");
					candidates = QueryEDSMSystem(target.Substring(0, target.IndexOf(mymatch.Groups[1].Value)));
					finalcandidates = candidates.Where(x=>x.coords != null);
				}
			}
			return finalcandidates;
		}
        /* Attempts to calculate a distance in lightyears between two given systems.
        * This is done using EDSM coordinates.
        * TODO: Once done with testing, transition source to be a <List>TravelLog.
        * TODO: Wait - this is not smart. CalculateEDSMDistance should just give you
        * the distances between those two systems, not do any stunts with the source.
        * Move this functionality out of there, and leave CED requiring a valid source
        * coord - maybe even require source to be type EDSMCoords.
        */
        public double CalculateEDSMDistance(string source, string target)
		{
			EDSMCoords sourcecoords= new EDSMCoords();
			EDSMCoords targetcoords= new EDSMCoords();
			IEnumerable<EDSMSystem> candidates;
			if (source == target)
				return 0; /* Well, it COULD happen? People have been known to do stupid things. */
			foreach(TravelLog mysource in myTravelLog.Reverse())
			{
				if (mysource.system.coords == null)
				{
					AppendStatus("System in travellog has no coords:" + mysource.system.name);
				}
				else
				{
					AppendStatus("Found coord'ed system " + mysource.system.name+", using as source.");
					sourcecoords = mysource.system.coords;
				}
			}
			if(sourcecoords == null || source=="Fuelum")
			{
				AppendStatus("Search for travellog coordinated system failed, using Fuelum coords"); // Add a static Fuelum system reference so we don't have to query EDSM for it.
				sourcecoords = fuelumCoords;
			}
			candidates = QueryEDSMSystem(target);
			if (candidates == null || candidates.Count() <1)
			{
				AppendStatus("EDSM does not know that system. Widening search...");
				candidates = GetCandidateSystems(target);
			}
			if(candidates.FirstOrDefault().coords == null)
			{
				AppendStatus("Known system, but no coords. Widening search...");
				candidates = GetCandidateSystems(target);
			}
			if (candidates == null || candidates.Count() < 1)
			{
					//Still couldn't find something, abort.
					AppendStatus("Couldn't find a candidate system, aborting...");
					return -1;
			}
			else
			{
				AppendStatus("I got " + candidates.Count() + " systems with coordinates. Picking the first.");
				targetcoords = candidates.FirstOrDefault().coords;
			}
			if (sourcecoords != null && targetcoords != null)
			{
				AppendStatus("We have two sets of coords that we can use to find a distance.");
				double deltaX = sourcecoords.x - targetcoords.x;
				double deltaY = sourcecoords.y - targetcoords.y;
				double deltaZ = sourcecoords.z - targetcoords.z;
				double distance = (double)Math.Sqrt(deltaX * deltaX + deltaY * deltaY + deltaZ * deltaZ);
				AppendStatus("Distance should be " + distance.ToString());
				return distance;
			}
			else
			{
				AppendStatus("Failed to find target coords. Giving up.");
				return -1;
			}
		}

		private void button_Click_1(object sender, RoutedEventArgs e)
		{
			TriggerSystemChange("Lave");
			//TriggerSystemChange("Blaa Hypai AI-I b26-1");
			//DateTime testdate = DateTime.Now;
/*            myTravelLog.Add(new TravelLog{ system=new EDSMSystem(){ name = "Sol" }, lastvisited=testdate});
			myTravelLog.Add(new TravelLog { system = new EDSMSystem() { name = "Fuelum" }, lastvisited = testdate});
			myTravelLog.Add(new TravelLog { system = new EDSMSystem() { name= "Leesti" }, lastvisited = testdate}); */
			//AppendStatus("Travellog now contains " + myTravelLog.Count() + " systems. Timestamp of first is " + myTravelLog.First().lastvisited +" name "+myTravelLog.First().system.name);
			CalculateEDSMDistance("Sol", SystemName.Text);
		}

				private void MenuItem_Click_1(object sender, RoutedEventArgs e)
				{
					//open the dispatch interface
					DispatchInterface.DispatchMain dlg = new DispatchInterface.DispatchMain();
					dlg.Show();
				}
	}
}