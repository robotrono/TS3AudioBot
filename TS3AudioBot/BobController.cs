// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2016  TS3AudioBot contributors
// 
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU Affero General Public License as
// published by the Free Software Foundation, either version 3 of the
// License, or (at your option) any later version.
// 
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Affero General Public License for more details.
// 
// You should have received a copy of the GNU Affero General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

namespace TS3AudioBot
{
	using System;
	using System.Collections.Generic;
	using System.Globalization;
	using System.Linq;
	using Helper;
	using TS3Client;
	using TS3Client.Messages;

	[Obsolete]
	public sealed class BobController : IPlayerConnection, ITargetManager
	{
		private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);
		private static readonly TimeSpan BobTimeout = TimeSpan.FromSeconds(60);

		private TeamspeakControl queryConnection;
		private BobControllerData bobControllerData;
		private TickWorker timeout;
		private DateTime lastUpdate = Util.GetNow();
		private WaitEventBlock<MusicData> musicInfoWaiter;
		public MusicData CurrentMusicInfo { get; private set; }

		private bool awaitingConnect;
		private bool isRunning;
		private Queue<string> commandQueue;
		private readonly object lockObject = new object();
		private ClientData bobClient;

		private Dictionary<ulong, SubscriptionData> channelSubscriptions;

		private bool sending = false;
		public bool Sending
		{
			get { return sending; }
			set
			{
				sending = value;
				SendMessage("audio " + (value ? "on" : "off"));
			}
		}

		#region IPlayerConnection

		private int volume = -1;
		private bool repeated = false;
		private bool pause = false;

		public event EventHandler OnSongEnd;

		public R<int> GetVolume() => volume;
		public void SetVolume(int value)
		{
			volume = value;
			SendMessage("music volume " + (value / 100d).ToString(CultureInfo.InvariantCulture));
		}

		public R<TimeSpan> GetPosition()
		{
			SendMessage("status music");
			try { musicInfoWaiter.Wait(RequestTimeout); } catch (TimeoutException) { return "Request timed out"; }
			return CurrentMusicInfo.Position;
		}
		public void SetPosition(TimeSpan value)
		{
			SendMessage("music seek " + value.TotalSeconds.ToString(CultureInfo.InvariantCulture));
		}

		public R<bool> IsRepeated() => repeated;
		public void SetRepeated(bool value)
		{
			repeated = value;
			SendMessage("music loop " + (value ? "on" : "off"));
		}

		public R<bool> IsPaused() => pause;
		public void SetPaused(bool value)
		{
			pause = value;
			SendMessage("music " + (value ? "pause" : "unpause"));
		}

		public R<TimeSpan> GetLength()
		{
			SendMessage("status music");
			try { musicInfoWaiter.Wait(RequestTimeout); } catch (TimeoutException) { return "Request timed out"; }
			return CurrentMusicInfo.Length;
		}

		public R<bool> IsPlaying()
		{
			SendMessage("status music");
			try { musicInfoWaiter.Wait(RequestTimeout); } catch (TimeoutException) { return "Request timed out"; }
			return CurrentMusicInfo.Status == MusicStatus.Playing;
		}


		public R AudioStart(string url) => SendMessage("music start " + url);
		public R AudioStop() => SendMessage("music stop");

		public void Initialize() { }

		#endregion

		public BobController(BobControllerData data, TeamspeakControl queryConnection)
		{
			if (queryConnection == null)
				throw new ArgumentNullException(nameof(queryConnection));

			timeout = TickPool.RegisterTick(TimeoutCheck, TimeSpan.FromMilliseconds(100), false);
			musicInfoWaiter = new WaitEventBlock<MusicData>();
			isRunning = false;
			awaitingConnect = false;
			bobControllerData = data;
			this.queryConnection = queryConnection;
			queryConnection.OnMessageReceived += GetResponse;
			queryConnection.OnClientConnect += OnBobConnect;
			queryConnection.OnClientDisconnect += OnBobDisconnnect;
			Util.Init(ref commandQueue);
			Util.Init(ref channelSubscriptions);
		}

		#region SendMethods

		public R SendMessage(string message)
		{
			if (isRunning)
			{
				lock (lockObject)
					return SendMessageRaw(message);
			}
			else
			{
				Log.Write(Log.Level.Debug, "BC Enqueing: {0}", message);
				commandQueue.Enqueue(message);
				return R.OkR;
			}
		}

		private R SendMessageRaw(string message)
		{
			if (bobClient == null)
			{
				Log.Write(Log.Level.Error, "BC bobClient is null! Message is lost: {0}", message);
				return "Internal BobController Error: bobClient == null";
			}

			Log.Write(Log.Level.Debug, "BC sending to bobC: {0}", message);
			try
			{
				queryConnection.SendMessage(message, bobClient.ClientId);
				return R.OkR;
			}
			catch (Ts3CommandException qcex)
			{
				Log.Write(Log.Level.Error, "BC failed to send to bobC ({0})", qcex.Message);
				return R.Err(qcex.Message);
			}
		}

		private R SendQueue()
		{
			if (!isRunning)
				throw new InvalidOperationException("The bob must run to send the commandQueue");

			lock (lockObject)
				while (commandQueue.Count > 0)
				{
					var result = SendMessageRaw(commandQueue.Dequeue());
					if (!result) return result;
				}
			return R.OkR;
		}

		#endregion

		#region Response

		public void Callback(bool enable) => SendMessage("callback " + (enable ? "on" : "off"));

		internal void GetResponse(object sender, TextMessage message)
		{
			if (bobClient == null)
				return;
			if (message.InvokerId != bobClient.ClientId)
				return;

			ParseData(TextUtil.RemoveUrlBB(message.Message));
		}

		private void ParseData(string input)
		{
			var splits = input.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(s => s.Split(new[] { ' ' }, 2));
			var typeKVP = splits.FirstOrDefault();
			if (typeKVP == null)
				throw new InvalidOperationException("Empty response");
			splits = splits.Skip(1);
			switch (typeKVP[0])
			{
			case "error": Log.Write(Log.Level.Warning, "Erroneous answer: {0}", typeKVP[1]); break;
			case "answer":
				switch (typeKVP[1])
				{
				case "music": musicInfoWaiter.Notify(CurrentMusicInfo = ParseMusicData(splits)); break;
				case "audio": break;
				case "end_event": break;
				default: throw new NotSupportedException("Answer not recognized");
				}
				break;
			case "callback":
				switch (typeKVP[1])
				{
				// Error during decoding (can be ignored)
				case "musicdecodeerror": break;
				// Fatal error, song cannot be started/continued
				case "musicreaderror":
				// song has finished
				case "musicfinished":
					OnSongEnd?.Invoke(this, new EventArgs());
					break;
				default: throw new NotSupportedException("Callback not recognized");
				}
				break;
			case "pong": Log.Write(Log.Level.Debug, "Alrighty then!"); break;
			default: throw new NotSupportedException("Response not recognized");
			}
		}

		private static MusicData ParseMusicData(IEnumerable<string[]> input)
		{
			var musicData = new MusicData();
			foreach (var result in input)
			{
				switch (result[0])
				{
				case "address": musicData.Address = result[1]; break;
				case "length":
					double length = double.Parse(result[1], CultureInfo.InvariantCulture);
					// prevent exceptions when dealing with live streams
					musicData.Length = TimeSpan.FromSeconds(Math.Max(0, length));
					break;
				case "loop": musicData.Loop = result[1] != "off"; break;
				case "position": musicData.Position = TimeSpan.FromSeconds(double.Parse(result[1], CultureInfo.InvariantCulture)); break;
				case "status": musicData.Status = (MusicStatus)Enum.Parse(typeof(MusicStatus), result[1], true); break;
				case "title": musicData.Title = result[1]; break;
				case "volume": musicData.Volume = double.Parse(result[1], CultureInfo.InvariantCulture); break;
				default: Log.Write(Log.Level.Warning, "Unparsed key: {0}={1}", result[0], result[1]); break;
				}
			}
			return musicData;
		}

		#endregion

		#region Bob & Events

		public void OnResourceStarted(object sender, PlayInfoEventArgs playData)
		{
			Log.Write(Log.Level.Debug, "BC Ressource started");
			BobStart();
			SetPaused(false);
			Sending = true;
			RestoreSubscriptions(playData.Invoker);
		}

		public void OnResourceStopped(object sender, EventArgs e)
		{
			Sending = false;
			BobStop();
		}

		private void BobStart()
		{
			bool timeoutStatus = timeout.Active;
			timeout.Active = false;
			lock (lockObject)
			{
				if (!isRunning && !awaitingConnect)
				{
					Callback(true);
					awaitingConnect = true;
					Log.Write(Log.Level.Debug, "BC now we are waiting for the bob");

					if (!Util.Execute(bobControllerData.startTSClient))
					{
						Log.Write(Log.Level.Debug, "BC could not start bob");
						awaitingConnect = false;
						timeout.Active = timeoutStatus;
					}
				}
			}
		}

		private void BobStop()
		{
			Log.Write(Log.Level.Debug, "BC initialted Bob-stop (isRunning={0})", isRunning);
			if (isRunning)
			{
				HasUpdate();
				timeout.Active = true;
				Log.Write(Log.Level.Debug, "BC start timeout");
			}
		}

		private void BobExit()
		{
			if (isRunning)
			{
				Log.Write(Log.Level.Info, "BC Exiting bob");
				SendMessage("exit");
			}
		}

		private void OnBobConnect(object sender, ClientEnterView e)
		{
			if (!awaitingConnect) return;

			Log.Write(Log.Level.Debug, "BC user entered with GrId {0}", e.ServerGroups);
			if (e.ServerGroups.Contains(bobControllerData.bobGroupId))
			{
				Log.Write(Log.Level.Debug, "BC user with correct UID found");
				bobClient = Generator.ActivateResponse<ClientData>();
				{
					bobClient.ChannelId = e.TargetChannelId;
					bobClient.DatabaseId = e.DatabaseId;
					bobClient.ClientId = e.ClientId;
					bobClient.NickName = e.NickName;
					bobClient.ClientType = e.ClientType;
				}
				isRunning = true;
				awaitingConnect = false;
				Log.Write(Log.Level.Debug, "BC bob is now officially running");
				SendQueue();
			}
		}

		private void OnBobDisconnnect(object sender, ClientLeftView e)
		{
			if (bobClient == null || e.ClientId != bobClient.ClientId) return;

			bobClient = null;
			isRunning = false;
			commandQueue.Clear();
			timeout.Active = false;
			Log.Write(Log.Level.Debug, "BC bob is now officially dead");
		}

		public void HasUpdate()
		{
			lastUpdate = Util.GetNow();
		}

		private void TimeoutCheck()
		{
			if (lastUpdate + BobTimeout < Util.GetNow())
			{
				Log.Write(Log.Level.Debug, "BC Timeout ran out...");
				BobExit();
			}
		}

		#endregion

		#region Subscriptions

		/// <summary>Adds a channel to the audio streaming list.</summary>
		/// <param name="channel">The id of the channel.</param>
		/// <param name="manual">Should be true if the command was invoked by a user,
		/// or false if the channel is added automatically by a play command.</param>
		public void WhisperChannelSubscribe(ulong channel, bool manual)
		{
			SendMessage("whisper channel add " + channel);
			SubscriptionData subscriptionData;
			if (!channelSubscriptions.TryGetValue(channel, out subscriptionData))
			{
				subscriptionData = new SubscriptionData { Id = channel, Manual = manual };
				channelSubscriptions.Add(channel, subscriptionData);
			}
			subscriptionData.Enabled = true;
			subscriptionData.Manual = subscriptionData.Manual || manual;
		}

		/// <summary>Removes a channel from the audio streaming list.</summary>
		/// <param name="channel">The id of the channel.</param>
		/// <param name="manual">Should be true if the command was invoked by a user,
		/// or false if the channel was removed automatically by an internal stop.</param>
		public void WhisperChannelUnsubscribe(ulong channel, bool manual)
		{
			SendMessage("whisper channel remove " + channel);
			SubscriptionData subscriptionData;
			if (!channelSubscriptions.TryGetValue(channel, out subscriptionData))
			{
				subscriptionData = new SubscriptionData { Id = channel, Manual = false };
				channelSubscriptions.Add(channel, subscriptionData);
			}
			if (manual)
			{
				subscriptionData.Manual = true;
				subscriptionData.Enabled = false;
			}
			else if (!subscriptionData.Manual)
			{
				subscriptionData.Enabled = false;
			}
		}

		public void WhisperClientSubscribe(ushort userId)
		{
			SendMessage("whisper client add " + userId);
		}

		public void WhisperClientUnsubscribe(ushort userId)
		{
			SendMessage("whisper client remove " + userId);
		}

		private void RestoreSubscriptions(ClientData invokingUser)
		{
			WhisperChannelSubscribe(invokingUser.ChannelId, false);
			foreach (var data in channelSubscriptions)
			{
				if (data.Value.Enabled)
				{
					if (data.Value.Manual)
						WhisperChannelSubscribe(data.Value.Id, false);
					else if (!data.Value.Manual && invokingUser.ChannelId != data.Value.Id)
						WhisperChannelUnsubscribe(data.Value.Id, false);
				}
			}
		}

		#endregion

		public void Dispose()
		{
			if (musicInfoWaiter != null)
			{
				musicInfoWaiter.Dispose();
				musicInfoWaiter = null;
			}
			BobExit();
			isRunning = false;
		}
	}

	public class SubscriptionData
	{
		public ulong Id { get; set; }
		public bool Enabled { get; set; }
		public bool Manual { get; set; }
	}

	public class MusicData
	{
		public MusicStatus Status { get; set; }
		public TimeSpan Length { get; set; }
		public TimeSpan Position { get; set; }
		public string Title { get; set; }
		public string Address { get; set; }
		public bool Loop { get; set; }
		public double Volume { get; set; }
	}

	public enum MusicStatus
	{
		Off,
		Playing,
		Paused,
		Finished,
		Error,
	}

	public struct BobControllerData
	{
		[Info("ServerGroupID of the ServerBob")]
		public int bobGroupId;
		[Info("the path to a launch script or the teamspeak3 executable itself")]
		public string startTSClient;
	}
}