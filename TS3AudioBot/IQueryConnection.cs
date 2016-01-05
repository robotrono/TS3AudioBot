﻿using System;
using TS3Query;
using TS3Query.Messages;

namespace TS3AudioBot
{
	public interface IQueryConnection : IDisposable
	{
		event EventHandler<TextMessage> OnMessageReceived;
		event EventHandler<ClientEnterView> OnClientConnect;
		event EventHandler<ClientLeftView> OnClientDisconnect;
		
		void Connect();

		void SendMessage(string message, ClientData client);
		void SendGlobalMessage(string message);
		void KickClientFromServer(int clientId);
		void KickClientFromChannel(int clientId);

		ClientData GetClientById(int id);
		ClientData GetClientByName(string name);
		void RefreshClientBuffer(bool force);
		int[] GetClientServerGroups(ClientData client);
	}
}
