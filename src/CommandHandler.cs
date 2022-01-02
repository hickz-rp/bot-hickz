using System;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Discord.Commands;
using Discord.WebSocket;
using Newtonsoft.Json.Linq;
using Discord;
using System.Linq;
using Newtonsoft.Json;
using Microsoft.Extensions.Configuration;
using System.Collections.Generic;
using System.Diagnostics;

namespace Hickz
{
	public partial class CommandHandlingService
    {
		private readonly CommandService _commands;
        private readonly DiscordSocketClient _client;
        private readonly IServiceProvider _services;

		public CommandHandlingService(IServiceProvider services)
        {
			_commands = services.GetRequiredService<CommandService>();
            _client = services.GetRequiredService<DiscordSocketClient>();
            _services = services;

            // Event handlers
            _client.Ready += ClientReadyAsync;
            _client.MessageReceived += HandleCommandAsync;
        }

        private async Task HandleCommandAsync(SocketMessage rawMessage)
        {
			if (rawMessage.Author.IsBot || !(rawMessage is SocketUserMessage message))
				return;

			if (PersistentMessages.persistentMessages != null)
			{
				JObject cfg = Functions.GetConfig();
				var channelInfo = rawMessage.Channel as SocketGuildChannel;
				if (channelInfo.Guild.Id == JsonConvert.DeserializeObject<ulong>(cfg["hickzDiscordServerId"].ToString()))
				{
					List<ulong> needModification = new List<ulong>();
					foreach (var (key, value) in PersistentMessages.persistentMessages)
					{
						if (rawMessage.Channel.Id == key)
						{
							needModification.Add(key);
						}
					}

					if (needModification.Count > 0)
					{
						foreach (ulong channelId in needModification)
						{
							await rawMessage.Channel.GetCachedMessage(PersistentMessages.persistentMessages[channelId].lastMessage).DeleteAsync();

							PersistentMessages.persistentMessages[channelId] = new PersistentMessages.StructPersistentMessages
							{
								embed = PersistentMessages.persistentMessages[channelId].embed,
								lastMessage = rawMessage.Channel.SendMessageAsync("", false, PersistentMessages.persistentMessages[channelId].embed.Build()).Result.Id
							};
						}
					}
				}
			}
			
			var context = new SocketCommandContext(_client, message);

			int argPos = 0;

			JObject config = Functions.GetConfig();
			string[] prefixes = JsonConvert.DeserializeObject<string[]>(config["prefixes"].ToString());

			// Check if message has any of the prefixes or mentiones the bot.
			if (prefixes.Any(x => message.HasStringPrefix(x, ref argPos)) || message.HasMentionPrefix(_client.CurrentUser, ref argPos))
			{
				// Execute the command.
				var result = await _commands.ExecuteAsync(context, argPos, _services);
				if (!result.IsSuccess) Console.WriteLine(result.ErrorReason);
			}
			else
			{
				if (rawMessage.Channel is IPrivateChannel) // Création de ticket de support si mp DM
				{
					Stopwatch watcher = new Stopwatch();

					var socketGuild = _client.GetGuild(JsonConvert.DeserializeObject<ulong>(config["hickzDiscordServerId"].ToString()));
					var socketCategoryChannel = socketGuild.GetCategoryChannel(JsonConvert.DeserializeObject<ulong>(config["hickzSupportCategoryId"].ToString()));

					ulong? currentTicketChannel = null;
					foreach(var categoryChannel in socketCategoryChannel.Channels)
					{
						var properties = categoryChannel as ITextChannel;
						if (properties.Topic == "Ticket d'assistance pour " + rawMessage.Author.Mention)
						{
							currentTicketChannel = properties.Id;
							break;
						}
					}

					if (currentTicketChannel == null)
					{
						var channelPermisssions = new ChannelPermissions(false, false, false, false, false);

						OverwritePermissions perms = new OverwritePermissions(
							viewChannel: PermValue.Allow,
							sendMessages: PermValue.Allow,
							attachFiles: PermValue.Allow,
							readMessageHistory: PermValue.Allow);

						var channel = await socketGuild.CreateTextChannelAsync("aide-" + rawMessage.Author.Username, prop => {
							prop.CategoryId = JsonConvert.DeserializeObject<ulong>(config["hickzSupportCategoryId"].ToString());
							prop.Topic = "Ticket d'assistance pour " + rawMessage.Author.Mention;
							prop.PermissionOverwrites = new List<Overwrite>
							{
								new Overwrite(socketGuild.EveryoneRole.Id, PermissionTarget.Role, new OverwritePermissions(viewChannel: PermValue.Deny)),
								new Overwrite(JsonConvert.DeserializeObject<ulong>(config["hickzSupportRoleId"].ToString()), PermissionTarget.Role, perms),
								new Overwrite(rawMessage.Author.Id, PermissionTarget.User, perms)
							};
						});

						var embed = new EmbedBuilder
						{
							Color = Color.DarkTeal,
							Title = "📩 • Ticket de support",
							Description = $"Message envoyé à la demande d'ouverture :\n\n{rawMessage.Content}",
							Timestamp = DateTime.Now,
							Footer = new EmbedFooterBuilder()
							{
								IconUrl = Functions.GetAvatarUrl(rawMessage.Author, 32),
								Text = rawMessage.Author.Username + "#" + rawMessage.Author.Discriminator
							}
						};

						var supportMessage = await channel.SendMessageAsync(text: socketGuild.GetRole(JsonConvert.DeserializeObject<ulong>(config["hickzSupportRoleId"].ToString())).Mention, embed: embed.Build());
						supportMessage.PinAsync().Wait();
						await context.Message.ReplyAsync($"Ticket créé, rendez-vous dans : {channel.Mention} 👋");
					}
					else
					{
						await context.Message.ReplyAsync($"Vous avez déjà un ticket de créé, rendez-vous dans : <#{currentTicketChannel}> 👋");
					}
				}
			}
		}

        private async Task ClientReadyAsync()
            => await Functions.SetBotStatusAsync(_client);

        public async Task InitializeAsync()
            => await _commands.AddModulesAsync(Assembly.GetEntryAssembly(), _services);
    }
}