using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using System.Collections.Concurrent;
using System.IO;
using Serilog;
using System.Reflection.Metadata.Ecma335;

namespace CustomLobbyServer
{
    // 启动程序
    public class Program
    {
        public static void Main(string[] args)
        {
            CreateHostBuilder(args).Build().Run();
        }

        public static IHostBuilder CreateHostBuilder(string[] args) =>
            Host.CreateDefaultBuilder(args)
                .ConfigureWebHostDefaults(webBuilder =>
                {
                    webBuilder.UseStartup<Startup>();
                    webBuilder.UseUrls("http://0.0.0.0:38888");
                });
    }

    // 启动配置
    public class Startup
    {
        public void ConfigureServices(IServiceCollection services)
        {
            services.AddSingleton<LobbyManager>();
            services.AddSingleton<PlayerStatsManager>();
            services.AddSingleton<PlayerNameManager>();

            // 注册后台统计服务
            services.AddHostedService<LobbyStatisticsService>();

            // 新增：注册玩家活动监控服务
            services.AddHostedService<PlayerActivityMonitorService>();

            // 确保在应用程序关闭时保存数据
            services.AddHostedService<ApplicationLifetimeService>();

            services.AddLogging(builder => { builder.AddConsole(); });

            // 配置 Serilog
            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Information()
                .WriteTo.Console()
                .WriteTo.File("Logs/lobby-.log", rollingInterval: RollingInterval.Day)
                .CreateLogger();

            // 添加 Serilog 到 ASP.NET Core
            services.AddSingleton(Log.Logger);
        }

        public void Configure(IApplicationBuilder app, IWebHostEnvironment env, ILogger<Startup> logger)
        {
            if (env.IsDevelopment())
            {
                app.UseDeveloperExceptionPage();
            }

            app.UseRouting();

            app.UseEndpoints(endpoints =>
            {
                // 创建大厅
                endpoints.MapPost("/lobby/create", async context =>
                {
                    var lobbyManager = context.RequestServices.GetRequiredService<LobbyManager>();
                    var playerNameManager = context.RequestServices.GetRequiredService<PlayerNameManager>();
                    var logger = context.RequestServices.GetRequiredService<ILogger<Startup>>();

                    string playerID = await GetFormValue(context.Request, "playerID");
                    string playerName = await GetFormValue(context.Request, "playerName"); // 新增获取玩家名称
                    string lobbyTypeStr = await GetFormValue(context.Request, "lobbyType");
                    string maxMembersStr = await GetFormValue(context.Request, "maxMembers");

                    if (string.IsNullOrEmpty(playerID) ||
                        !int.TryParse(lobbyTypeStr, out int lobbyType) ||
                        !int.TryParse(maxMembersStr, out int maxMembers))
                    {
                        logger.LogWarning(
                            "Invalid create lobby parameters: playerID={PlayerID}, lobbyType={LobbyType}, maxMembers={MaxMembers}",
                            playerID, lobbyTypeStr, maxMembersStr);
                        await WriteErrorResponse(context.Response, "Invalid parameters");
                        return;
                    }

                    // 如果提供了玩家名称，则更新玩家名称
                    if (!string.IsNullOrEmpty(playerName))
                    {
                        playerNameManager.UpdatePlayerName(playerID, playerName);
                        logger.LogInformation("Updated name for player {PlayerID} to {Name} during lobby creation",
                            playerID, playerName);
                    }

                    var lobby = lobbyManager.CreateLobby(playerID, lobbyType, maxMembers);
                    logger.LogInformation("Created lobby: {LobbyID} by player {PlayerID}", lobby.ID, playerID);

                    await WriteSuccessResponse(context.Response, new Dictionary<string, string>
                    {
                        { "lobbyID", lobby.ID }
                    });
                });

                // 加入大厅
                endpoints.MapPost("/lobby/join", async context =>
                {
                    var lobbyManager = context.RequestServices.GetRequiredService<LobbyManager>();
                    var playerNameManager = context.RequestServices.GetRequiredService<PlayerNameManager>();
                    var logger = context.RequestServices.GetRequiredService<ILogger<Startup>>();

                    string playerID = await GetFormValue(context.Request, "playerID");
                    string playerName = await GetFormValue(context.Request, "playerName"); // 新增获取玩家名称
                    string lobbyID = await GetFormValue(context.Request, "lobbyID");

                    if (string.IsNullOrEmpty(playerID) || string.IsNullOrEmpty(lobbyID))
                    {
                        logger.LogWarning("Invalid join lobby parameters: playerID={PlayerID}, lobbyID={LobbyID}",
                            playerID, lobbyID);
                        await WriteErrorResponse(context.Response, "Invalid parameters");
                        return;
                    }

                    // 如果提供了玩家名称，则更新玩家名称
                    if (!string.IsNullOrEmpty(playerName))
                    {
                        playerNameManager.UpdatePlayerName(playerID, playerName);
                        logger.LogInformation("Updated name for player {PlayerID} to {Name} during lobby join",
                            playerID, playerName);
                    }

                    bool success = lobbyManager.JoinLobby(lobbyID, playerID);
                    if (success)
                    {
                        logger.LogInformation("Player {PlayerID} joined lobby {LobbyID}", playerID, lobbyID);
                        var lobby = lobbyManager.GetLobby(lobbyID);
                        await WriteSuccessResponse(context.Response, new Dictionary<string, string>
                        {
                            { "success", "true" },
                            { "locked", lobby.IsLocked ? "1" : "0" }
                        });
                    }
                    else
                    {
                        logger.LogWarning("Player {PlayerID} failed to join lobby {LobbyID}", playerID, lobbyID);
                        await WriteErrorResponse(context.Response, "Failed to join lobby");
                    }
                });

                // 离开大厅
                endpoints.MapPost("/lobby/leave", async context =>
                {
                    var lobbyManager = context.RequestServices.GetRequiredService<LobbyManager>();
                    var logger = context.RequestServices.GetRequiredService<ILogger<Startup>>();

                    string playerID = await GetFormValue(context.Request, "playerID");
                    string lobbyID = await GetFormValue(context.Request, "lobbyID");

                    if (string.IsNullOrEmpty(playerID) || string.IsNullOrEmpty(lobbyID))
                    {
                        logger.LogWarning("Invalid leave lobby parameters: playerID={PlayerID}, lobbyID={LobbyID}",
                            playerID, lobbyID);
                        await WriteErrorResponse(context.Response, "Invalid parameters");
                        return;
                    }

                    bool success = lobbyManager.LeaveLobby(lobbyID, playerID);
                    logger.LogInformation("Player {PlayerID} left lobby {LobbyID}, success: {Success}", playerID,
                        lobbyID, success);
                    await WriteSuccessResponse(context.Response, new Dictionary<string, string>
                    {
                        { "success", success ? "true" : "false" }
                    });
                });

                // 获取大厅列表
                endpoints.MapGet("/lobby/list", async context =>
                {
                    var lobbyManager = context.RequestServices.GetRequiredService<LobbyManager>();
                    var logger = context.RequestServices.GetRequiredService<ILogger<Startup>>();

                    Dictionary<string, string> filters = new Dictionary<string, string>();
                    foreach (var param in context.Request.Query)
                    {
                        filters[param.Key] = param.Value;
                    }

                    var lobbyIDs = lobbyManager.GetLobbyList(filters);
                    string lobbiesStr = string.Join(",", lobbyIDs);

                    logger.LogInformation(
                        "Requesting lobby list with {FilterCount} filters, found {LobbyCount} lobbies",
                        filters.Count, lobbyIDs.Count);

                    await WriteSuccessResponse(context.Response, new Dictionary<string, string>
                    {
                        { "lobbies", lobbiesStr }
                    });
                });

                // 获取大厅数据
                endpoints.MapGet("/lobby/data", async context =>
                {
                    var lobbyManager = context.RequestServices.GetRequiredService<LobbyManager>();
                    var logger = context.RequestServices.GetRequiredService<ILogger<Startup>>();

                    string lobbyID = context.Request.Query["lobbyID"];
                    string key = context.Request.Query["key"];
                    string playerID = context.Request.Query["playerID"];

                    if (string.IsNullOrEmpty(lobbyID) || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(playerID))
                    {
                        logger.LogWarning(
                            "Invalid get lobby data parameters: lobbyID={LobbyID}, key={Key}, playerID={PlayerID}",
                            lobbyID, key, playerID);
                        await WriteErrorResponse(context.Response, "Invalid parameters");
                        return;
                    }

                    string value = lobbyManager.GetLobbyData(lobbyID, key);
                    logger.LogInformation("Getting lobby data for lobby {LobbyID}, key {Key}, value {Value}",
                        lobbyID, key, value);

                    await WriteSuccessResponse(context.Response, new Dictionary<string, string>
                    {
                        { "value", value ?? "" }
                    });
                });

                // 设置大厅数据
                endpoints.MapPost("/lobby/data/set", async context =>
                {
                    var lobbyManager = context.RequestServices.GetRequiredService<LobbyManager>();
                    var logger = context.RequestServices.GetRequiredService<ILogger<Startup>>();

                    string lobbyID = await GetFormValue(context.Request, "lobbyID");
                    string key = await GetFormValue(context.Request, "key");
                    string value = await GetFormValue(context.Request, "value");
                    string playerID = await GetFormValue(context.Request, "playerID");

                    if (string.IsNullOrEmpty(lobbyID) || string.IsNullOrEmpty(key) || playerID == null)
                    {
                        logger.LogWarning(
                            "Invalid set lobby data parameters: lobbyID={LobbyID}, key={Key}, playerID={PlayerID}",
                            lobbyID, key, playerID);
                        await WriteErrorResponse(context.Response, "Invalid parameters");
                        return;
                    }

                    bool success = lobbyManager.SetLobbyData(lobbyID, key, value, playerID);
                    logger.LogInformation(
                        "Setting lobby data for lobby {LobbyID}, key {Key}, value {Value}, success: {Success}",
                        lobbyID, key, value, success);

                    await WriteSuccessResponse(context.Response, new Dictionary<string, string>
                    {
                        { "success", success ? "true" : "false" }
                    });
                });

                // 设置大厅类型
                endpoints.MapPost("/lobby/type/set", async context =>
                {
                    var lobbyManager = context.RequestServices.GetRequiredService<LobbyManager>();
                    var logger = context.RequestServices.GetRequiredService<ILogger<Startup>>();

                    string lobbyID = await GetFormValue(context.Request, "lobbyID");
                    string lobbyTypeStr = await GetFormValue(context.Request, "lobbyType");
                    string playerID = await GetFormValue(context.Request, "playerID");

                    if (string.IsNullOrEmpty(lobbyID) || string.IsNullOrEmpty(lobbyTypeStr) ||
                        string.IsNullOrEmpty(playerID) ||
                        !int.TryParse(lobbyTypeStr, out int lobbyType))
                    {
                        logger.LogWarning(
                            "Invalid set lobby type parameters: lobbyID={LobbyID}, lobbyType={LobbyType}, playerID={PlayerID}",
                            lobbyID, lobbyTypeStr, playerID);
                        await WriteErrorResponse(context.Response, "Invalid parameters");
                        return;
                    }

                    bool success = lobbyManager.SetLobbyType(lobbyID, lobbyType, playerID);
                    logger.LogInformation("Setting lobby type for lobby {LobbyID} to {LobbyType}, success: {Success}",
                        lobbyID, lobbyType, success);

                    await WriteSuccessResponse(context.Response, new Dictionary<string, string>
                    {
                        { "success", success ? "true" : "false" }
                    });
                });

                // 获取大厅成员数量
                endpoints.MapGet("/lobby/members/count", async context =>
                {
                    var lobbyManager = context.RequestServices.GetRequiredService<LobbyManager>();
                    var logger = context.RequestServices.GetRequiredService<ILogger<Startup>>();

                    string lobbyID = context.Request.Query["lobbyID"];
                    string playerID = context.Request.Query["playerID"];

                    if (string.IsNullOrEmpty(lobbyID) || string.IsNullOrEmpty(playerID))
                    {
                        logger.LogWarning(
                            "Invalid get lobby members count parameters: lobbyID={LobbyID}, playerID={PlayerID}",
                            lobbyID, playerID);
                        await WriteErrorResponse(context.Response, "Invalid parameters");
                        return;
                    }

                    int count = lobbyManager.GetLobbyMemberCount(lobbyID);
                    logger.LogInformation("Getting lobby member count for lobby {LobbyID}: {Count}", lobbyID, count);

                    await WriteSuccessResponse(context.Response, new Dictionary<string, string>
                    {
                        { "count", count.ToString() }
                    });
                });

                // 按索引获取大厅成员
                endpoints.MapGet("/lobby/members/get", async context =>
                {
                    var lobbyManager = context.RequestServices.GetRequiredService<LobbyManager>();
                    var logger = context.RequestServices.GetRequiredService<ILogger<Startup>>();

                    string lobbyID = context.Request.Query["lobbyID"];
                    string indexStr = context.Request.Query["index"];
                    string playerID = context.Request.Query["playerID"];

                    if (string.IsNullOrEmpty(lobbyID) || string.IsNullOrEmpty(indexStr) ||
                        string.IsNullOrEmpty(playerID) ||
                        !int.TryParse(indexStr, out int index))
                    {
                        logger.LogWarning(
                            "Invalid get lobby member parameters: lobbyID={LobbyID}, index={Index}, playerID={PlayerID}",
                            lobbyID, indexStr, playerID);
                        await WriteErrorResponse(context.Response, "Invalid parameters");
                        return;
                    }

                    string memberID = lobbyManager.GetLobbyMemberByIndex(lobbyID, index);
                    logger.LogInformation("Getting lobby member at index {Index} for lobby {LobbyID}: {MemberID}",
                        index, lobbyID, memberID);

                    await WriteSuccessResponse(context.Response, new Dictionary<string, string>
                    {
                        { "memberID", memberID ?? "" }
                    });
                });

                // 获取大厅拥有者
                endpoints.MapGet("/lobby/owner", async context =>
                {
                    var lobbyManager = context.RequestServices.GetRequiredService<LobbyManager>();
                    var logger = context.RequestServices.GetRequiredService<ILogger<Startup>>();

                    string lobbyID = context.Request.Query["lobbyID"];
                    string playerID = context.Request.Query["playerID"];

                    if (string.IsNullOrEmpty(lobbyID) || string.IsNullOrEmpty(playerID))
                    {
                        logger.LogWarning("Invalid get lobby owner parameters: lobbyID={LobbyID}, playerID={PlayerID}",
                            lobbyID, playerID);
                        await WriteErrorResponse(context.Response, "Invalid parameters");
                        return;
                    }

                    string ownerID = lobbyManager.GetLobbyOwner(lobbyID);
                    logger.LogInformation("Getting lobby owner for lobby {LobbyID}: {OwnerID}", lobbyID, ownerID);

                    await WriteSuccessResponse(context.Response, new Dictionary<string, string>
                    {
                        { "ownerID", ownerID ?? "" }
                    });
                });

                // 邀请用户到大厅
                endpoints.MapPost("/lobby/invite", async context =>
                {
                    var lobbyManager = context.RequestServices.GetRequiredService<LobbyManager>();
                    var logger = context.RequestServices.GetRequiredService<ILogger<Startup>>();

                    string lobbyID = await GetFormValue(context.Request, "lobbyID");
                    string inviterID = await GetFormValue(context.Request, "inviterID");
                    string inviteeID = await GetFormValue(context.Request, "inviteeID");

                    if (string.IsNullOrEmpty(lobbyID) || string.IsNullOrEmpty(inviterID) ||
                        string.IsNullOrEmpty(inviteeID))
                    {
                        logger.LogWarning(
                            "Invalid invite to lobby parameters: lobbyID={LobbyID}, inviterID={InviterID}, inviteeID={InviteeID}",
                            lobbyID, inviterID, inviteeID);
                        await WriteErrorResponse(context.Response, "Invalid parameters");
                        return;
                    }

                    bool success = lobbyManager.InviteToLobby(lobbyID, inviterID, inviteeID);
                    logger.LogInformation(
                        "Inviting player {InviteeID} to lobby {LobbyID} by {InviterID}, success: {Success}",
                        inviteeID, lobbyID, inviterID, success);

                    await WriteSuccessResponse(context.Response, new Dictionary<string, string>
                    {
                        { "success", success ? "true" : "false" }
                    });
                });

                // 轮询大厅更新
                endpoints.MapGet("/lobby/poll", async context =>
                {
                    var lobbyManager = context.RequestServices.GetRequiredService<LobbyManager>();
                    var logger = context.RequestServices.GetRequiredService<ILogger<Startup>>();

                    string lobbyID = context.Request.Query["lobbyID"];
                    string playerID = context.Request.Query["playerID"];

                    if (string.IsNullOrEmpty(lobbyID) || string.IsNullOrEmpty(playerID))
                    {
                        logger.LogWarning("Invalid lobby poll parameters: lobbyID={LobbyID}, playerID={PlayerID}",
                            lobbyID, playerID);
                        await WriteErrorResponse(context.Response, "Invalid parameters");
                        return;
                    }

                    // 新增：更新玩家活动时间
                    lobbyManager.UpdatePlayerActivity(playerID);

                    // Console.WriteLine($"player {playerID} polled update for lobby {lobbyID}");

                    var updates = lobbyManager.GetPendingUpdates(lobbyID, playerID);
                    if (updates != null && updates.Count > 0)
                    {
                        // 只返回第一个更新，客户端会再次轮询获取其他更新
                        var update = updates[0];
                        lobbyManager.AcknowledgeUpdate(lobbyID, playerID, update);

                        logger.LogInformation(
                            "Poll: Player {PlayerID} received update for lobby {LobbyID}, type: {UpdateType}",
                            playerID, lobbyID, update["type"]);

                        await WriteSuccessResponse(context.Response, update);
                    }
                    else
                    {
                        // 没有更新
                        await WriteSuccessResponse(context.Response, new Dictionary<string, string>());
                    }
                });

                // 获取玩家统计数据
                endpoints.MapGet("/player/stats", async context =>
                {
                    var playerStatsManager = context.RequestServices.GetRequiredService<PlayerStatsManager>();
                    var logger = context.RequestServices.GetRequiredService<ILogger<Startup>>();

                    string playerID = context.Request.Query["playerID"];

                    if (string.IsNullOrEmpty(playerID))
                    {
                        logger.LogWarning("Invalid get player stats parameters: playerID={PlayerID}", playerID);
                        await WriteErrorResponse(context.Response, "Invalid parameters");
                        return;
                    }

                    var stats = playerStatsManager.GetPlayerStats(playerID);
                    if (stats != null)
                    {
                        logger.LogInformation("Getting stats for player {PlayerID}: W{Win}-L{Lose}-D{Draw}, ELO:{ELO}",
                            playerID, stats.Win, stats.Lose, stats.Draw, stats.ELO);

                        await WriteSuccessResponse(context.Response, new Dictionary<string, string>
                        {
                            { "win", stats.Win.ToString() },
                            { "lose", stats.Lose.ToString() },
                            { "draw", stats.Draw.ToString() },
                            { "ELO", stats.ELO.ToString() }
                        });
                    }
                    else
                    {
                        logger.LogInformation("No stats found for player {PlayerID}", playerID);
                        await WriteSuccessResponse(context.Response, new Dictionary<string, string>());
                    }
                });

                // 更新玩家统计数据
                endpoints.MapPost("/player/stats/update", async context =>
                {
                    var playerStatsManager = context.RequestServices.GetRequiredService<PlayerStatsManager>();
                    var logger = context.RequestServices.GetRequiredService<ILogger<Startup>>();

                    string playerID = await GetFormValue(context.Request, "playerID");
                    string winStr = await GetFormValue(context.Request, "win");
                    string loseStr = await GetFormValue(context.Request, "lose");
                    string drawStr = await GetFormValue(context.Request, "draw");
                    string eloStr = await GetFormValue(context.Request, "ELO");

                    if (string.IsNullOrEmpty(playerID) ||
                        !int.TryParse(winStr, out int win) ||
                        !int.TryParse(loseStr, out int lose) ||
                        !int.TryParse(drawStr, out int draw) ||
                        !int.TryParse(eloStr, out int elo))
                    {
                        logger.LogWarning(
                            "Invalid update player stats parameters: playerID={PlayerID}, win={Win}, lose={Lose}, draw={Draw}, ELO={ELO}",
                            playerID, winStr, loseStr, drawStr, eloStr);
                        await WriteErrorResponse(context.Response, "Invalid parameters");
                        return;
                    }

                    bool success = playerStatsManager.UpdatePlayerStats(playerID, win, lose, draw, elo);
                    logger.LogInformation(
                        "Updated stats for player {PlayerID}: W{Win}-L{Lose}-D{Draw}, ELO:{ELO}, success: {Success}",
                        playerID, win, lose, draw, elo, success);

                    await WriteSuccessResponse(context.Response, new Dictionary<string, string>
                    {
                        { "success", success ? "true" : "false" }
                    });
                });

                // 获取玩家名称
                endpoints.MapGet("/player/name", async context =>
                {
                    var playerNameManager = context.RequestServices.GetRequiredService<PlayerNameManager>();
                    var logger = context.RequestServices.GetRequiredService<ILogger<Startup>>();

                    string playerID = context.Request.Query["playerID"];

                    if (string.IsNullOrEmpty(playerID))
                    {
                        logger.LogWarning("Invalid get player name parameters: playerID={PlayerID}", playerID);
                        await WriteErrorResponse(context.Response, "Invalid parameters");
                        return;
                    }

                    string name = playerNameManager.GetPlayerName(playerID);
                    logger.LogInformation("Getting name for player {PlayerID}: {Name}", playerID, name ?? "not found");

                    await WriteSuccessResponse(context.Response, new Dictionary<string, string>
                    {
                        { "name", name ?? "" }
                    });
                });

                // 更新玩家名称
                endpoints.MapPost("/player/name/update", async context =>
                {
                    var playerNameManager = context.RequestServices.GetRequiredService<PlayerNameManager>();
                    var logger = context.RequestServices.GetRequiredService<ILogger<Startup>>();

                    string playerID = await GetFormValue(context.Request, "playerID");
                    string name = await GetFormValue(context.Request, "name");

                    if (string.IsNullOrEmpty(playerID) || string.IsNullOrEmpty(name))
                    {
                        logger.LogWarning("Invalid update player name parameters: playerID={PlayerID}, name={Name}",
                            playerID, name);
                        await WriteErrorResponse(context.Response, "Invalid parameters");
                        return;
                    }

                    bool success = playerNameManager.UpdatePlayerName(playerID, name);
                    logger.LogInformation("Updated name for player {PlayerID} to {Name}, success: {Success}", playerID,
                        name, success);

                    await WriteSuccessResponse(context.Response, new Dictionary<string, string>
                    {
                        { "success", success ? "true" : "false" }
                    });
                });

                // 获取总在线人数
                endpoints.MapGet("/server/online-count", async context =>
                {
                    var lobbyManager = context.RequestServices.GetRequiredService<LobbyManager>();
                    var logger = context.RequestServices.GetRequiredService<ILogger<Startup>>();

                    var allLobbies = lobbyManager.GetAllLobbies();
                    var onlinePlayerIDs = new HashSet<string>();

                    // 统计所有大厅中的不重复玩家
                    foreach (var lobby in allLobbies)
                    {
                        foreach (var member in lobby.Members)
                        {
                            onlinePlayerIDs.Add(member);
                        }
                    }

                    int onlineCount = onlinePlayerIDs.Count;

                    logger.LogInformation("Total online players: {OnlineCount}", onlineCount);

                    await WriteSuccessResponse(context.Response, new Dictionary<string, string>
                    {
                        { "onlineCount", onlineCount.ToString() },
                        { "totalLobbies", allLobbies.Count.ToString() }
                    });
                });

                // 提供Web UI页面
                endpoints.MapGet("/", async context => { await context.Response.WriteAsync(GetDashboardHtml()); });

                // 获取服务器状态API
                endpoints.MapGet("/api/server/status", async context =>
                {
                    var lobbyManager = context.RequestServices.GetRequiredService<LobbyManager>();
                    var playerStatsManager = context.RequestServices.GetRequiredService<PlayerStatsManager>();
                    var playerNameManager = context.RequestServices.GetRequiredService<PlayerNameManager>();

                    var allLobbies = lobbyManager.GetAllLobbies();
                    var onlinePlayerIDs = new HashSet<string>();

                    foreach (var lobby in allLobbies)
                    {
                        foreach (var member in lobby.Members)
                        {
                            onlinePlayerIDs.Add(member);
                        }
                    }

                    var stats = new
                    {
                        onlineCount = onlinePlayerIDs.Count,
                        totalLobbies = allLobbies.Count,
                        publicLobbies = allLobbies.Count(l => l.LobbyType == 2),
                        privateLobbies = allLobbies.Count(l => l.LobbyType == 0),
                        friendLobbies = allLobbies.Count(l => l.LobbyType == 1),
                        timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")
                    };

                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonConvert.SerializeObject(stats));
                });

                // 获取详细大厅信息API
                endpoints.MapGet("/api/lobbies/detailed", async context =>
                {
                    var lobbyManager = context.RequestServices.GetRequiredService<LobbyManager>();
                    var playerNameManager = context.RequestServices.GetRequiredService<PlayerNameManager>();
                    var playerStatsManager = context.RequestServices.GetRequiredService<PlayerStatsManager>();

                    var allLobbies = lobbyManager.GetAllLobbies();
                    var detailedLobbies = allLobbies.Select(lobby => new
                    {
                        id = lobby.ID,
                        ownerId = lobby.OwnerID,
                        ownerName = playerNameManager.GetPlayerName(lobby.OwnerID) ?? "Unknown",
                        ownerStats = playerStatsManager.GetPlayerStats(lobby.OwnerID),
                        type = lobby.LobbyType,
                        typeName = GetLobbyTypeName(lobby.LobbyType),
                        maxMembers = lobby.MaxMembers,
                        currentMembers = lobby.Members.Count,
                        isLocked = lobby.IsLocked,
                        members = lobby.Members.Select(memberId => new
                        {
                            id = memberId,
                            name = playerNameManager.GetPlayerName(memberId) ?? "Unknown",
                            stats = playerStatsManager.GetPlayerStats(memberId),
                            isOwner = memberId == lobby.OwnerID
                        }).ToList(),
                        data = lobby.Data.ToDictionary(kvp => kvp.Key, kvp => kvp.Value)
                    }).ToList();

                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonConvert.SerializeObject(detailedLobbies));
                });

                // 获取在线玩家列表API
                endpoints.MapGet("/api/players/online", async context =>
                {
                    var lobbyManager = context.RequestServices.GetRequiredService<LobbyManager>();
                    var playerNameManager = context.RequestServices.GetRequiredService<PlayerNameManager>();
                    var playerStatsManager = context.RequestServices.GetRequiredService<PlayerStatsManager>();

                    var allLobbies = lobbyManager.GetAllLobbies();
                    var onlinePlayerIDs = new HashSet<string>();

                    foreach (var lobby in allLobbies)
                    {
                        foreach (var member in lobby.Members)
                        {
                            onlinePlayerIDs.Add(member);
                        }
                    }

                    var onlinePlayers = onlinePlayerIDs.Select(playerId => new
                    {
                        id = playerId,
                        name = playerNameManager.GetPlayerName(playerId) ?? "Unknown",
                        stats = playerStatsManager.GetPlayerStats(playerId),
                        currentLobby = allLobbies.FirstOrDefault(l => l.Members.Contains(playerId))?.ID
                    }).OrderByDescending(p => p.stats?.ELO ?? 0).ToList();

                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonConvert.SerializeObject(onlinePlayers));
                });

                // 在现有的API端点后面添加这个管理API
                endpoints.MapPost("/api/admin/kick-player", async context =>
                {
                    var lobbyManager = context.RequestServices.GetRequiredService<LobbyManager>();
                    var logger = context.RequestServices.GetRequiredService<ILogger<Startup>>();

                    string lobbyID = await GetFormValue(context.Request, "lobbyID");
                    string playerID = await GetFormValue(context.Request, "playerID");

                    if (string.IsNullOrEmpty(lobbyID) || string.IsNullOrEmpty(playerID))
                    {
                        logger.LogWarning("Invalid kick player parameters: lobbyID={LobbyID}, playerID={PlayerID}",
                            lobbyID, playerID);
                        await WriteErrorResponse(context.Response, "Invalid parameters");
                        return;
                    }

                    // 使用现有的LeaveLobby方法来踢出玩家
                    bool success = lobbyManager.LeaveLobby(lobbyID, playerID);

                    if (success)
                    {
                        logger.LogWarning("Admin action: Player {PlayerID} was kicked from lobby {LobbyID}", playerID,
                            lobbyID);
                    }
                    else
                    {
                        logger.LogWarning("Admin action failed: Could not kick player {PlayerID} from lobby {LobbyID}",
                            playerID, lobbyID);
                    }

                    context.Response.ContentType = "application/json";
                    await context.Response.WriteAsync(JsonConvert.SerializeObject(new { success = success }));
                });
            });

            logger.LogInformation("Custom Lobby Server started on port 38888");
        }

        // 从表单获取值
        private static async Task<string> GetFormValue(HttpRequest request, string key)
        {
            if (request.HasFormContentType)
            {
                var form = await request.ReadFormAsync();
                return form[key].ToString();
            }

            return null;
        }

        // 写入成功响应
        private static async Task WriteSuccessResponse(HttpResponse response, Dictionary<string, string> data)
        {
            response.ContentType = "text/plain";
            response.StatusCode = 200;

            // 将字典转换为URL编码形式
            var parts = data.Select(kvp => $"{Uri.EscapeDataString(kvp.Key)}={Uri.EscapeDataString(kvp.Value ?? "")}");
            await response.WriteAsync(string.Join("&", parts));
        }

        // 写入错误响应
        private static async Task WriteErrorResponse(HttpResponse response, string errorMessage)
        {
            response.ContentType = "text/plain";
            response.StatusCode = 400;
            await response.WriteAsync($"error={Uri.EscapeDataString(errorMessage)}");
        }

        private static string GetLobbyTypeName(int type)
        {
            return type switch
            {
                0 => "Private",
                1 => "Friends",
                2 => "Public",
                _ => "Unknown"
            };
        }

        private static string GetDashboardHtml()
        {
            return @"
<!DOCTYPE html>
<html lang=""en"">
<head>
    <meta charset=""UTF-8"">
    <meta name=""viewport"" content=""width=device-width, initial-scale=1.0"">
    <title>Lobby Server Dashboard</title>
    <style>
        * {
            margin: 0;
            padding: 0;
            box-sizing: border-box;
        }
        
        body {
            font-family: 'Segoe UI', Tahoma, Geneva, Verdana, sans-serif;
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            min-height: 100vh;
            color: #333;
        }
        
        .container {
            max-width: 1400px;
            margin: 0 auto;
            padding: 20px;
        }
        
        .header {
            background: rgba(255, 255, 255, 0.95);
            border-radius: 15px;
            padding: 30px;
            margin-bottom: 30px;
            box-shadow: 0 10px 30px rgba(0, 0, 0, 0.2);
            text-align: center;
        }
        
        .header h1 {
            color: #4a5568;
            font-size: 2.5rem;
            margin-bottom: 10px;
            font-weight: 700;
        }
        
        .header p {
            color: #718096;
            font-size: 1.1rem;
        }
        
        .admin-mode-indicator {
            background: #e53e3e;
            color: white;
            padding: 8px 16px;
            border-radius: 20px;
            font-size: 0.9rem;
            font-weight: bold;
            margin-top: 10px;
            display: inline-block;
        }
        
        .stats-grid {
            display: grid;
            grid-template-columns: repeat(auto-fit, minmax(250px, 1fr));
            gap: 20px;
            margin-bottom: 30px;
        }
        
        .stat-card {
            background: rgba(255, 255, 255, 0.95);
            border-radius: 15px;
            padding: 25px;
            box-shadow: 0 8px 25px rgba(0, 0, 0, 0.15);
            transition: transform 0.3s ease, box-shadow 0.3s ease;
        }
        
        .stat-card:hover {
            transform: translateY(-5px);
            box-shadow: 0 15px 35px rgba(0, 0, 0, 0.2);
        }
        
        .stat-card h3 {
            color: #4a5568;
            font-size: 1rem;
            margin-bottom: 10px;
            text-transform: uppercase;
            letter-spacing: 1px;
        }
        
        .stat-value {
            font-size: 2.5rem;
            font-weight: bold;
            color: #667eea;
        }
        
        .main-content {
            display: grid;
            grid-template-columns: 1fr 1fr;
            gap: 30px;
        }
        
        .section {
            background: rgba(255, 255, 255, 0.95);
            border-radius: 15px;
            padding: 25px;
            box-shadow: 0 8px 25px rgba(0, 0, 0, 0.15);
        }
        
        .section h2 {
            color: #4a5568;
            margin-bottom: 20px;
            font-size: 1.5rem;
            border-bottom: 2px solid #e2e8f0;
            padding-bottom: 10px;
        }
        
        .lobby-item, .player-item {
            background: #f7fafc;
            border-radius: 10px;
            padding: 15px;
            margin-bottom: 15px;
            border-left: 4px solid #667eea;
            transition: all 0.3s ease;
        }
        
        .lobby-item:hover, .player-item:hover {
            background: #edf2f7;
            transform: translateX(5px);
        }
        
        .lobby-header {
            display: flex;
            justify-content: space-between;
            align-items: center;
            margin-bottom: 10px;
        }
        
        .lobby-id {
            font-family: 'Courier New', monospace;
            background: #667eea;
            color: white;
            padding: 4px 8px;
            border-radius: 5px;
            font-size: 0.8rem;
        }
        
        .lobby-type {
            padding: 4px 12px;
            border-radius: 20px;
            font-size: 0.8rem;
            font-weight: bold;
        }
        
        .type-public { background: #48bb78; color: white; }
        .type-private { background: #ed8936; color: white; }
        .type-friends { background: #4299e1; color: white; }
        
        .member-count {
            color: #718096;
            font-size: 0.9rem;
        }
        
        .player-name {
            font-weight: bold;
            color: #4a5568;
        }
        
        .player-stats {
            color: #718096;
            font-size: 0.9rem;
            margin-top: 5px;
        }
        
        .elo-high { color: #48bb78; font-weight: bold; }
        .elo-medium { color: #ed8936; font-weight: bold; }
        .elo-low { color: #f56565; font-weight: bold; }
        
        .refresh-btn {
            background: #667eea;
            color: white;
            border: none;
            padding: 12px 25px;
            border-radius: 25px;
            cursor: pointer;
            font-size: 1rem;
            font-weight: bold;
            transition: all 0.3s ease;
            margin: 20px auto;
            display: block;
        }
        
        .refresh-btn:hover {
            background: #5a67d8;
            transform: translateY(-2px);
            box-shadow: 0 5px 15px rgba(0, 0, 0, 0.2);
        }
        
        .kick-btn {
            background: #e53e3e;
            color: white;
            border: none;
            padding: 4px 8px;
            border-radius: 4px;
            cursor: pointer;
            font-size: 0.8rem;
            margin-left: 8px;
            transition: all 0.3s ease;
        }
        
        .kick-btn:hover {
            background: #c53030;
            transform: scale(1.05);
        }
        
        .kick-btn:disabled {
            background: #a0aec0;
            cursor: not-allowed;
            transform: none;
        }
        
        .admin-controls {
            margin-top: 10px;
            padding-top: 10px;
            border-top: 1px solid #e2e8f0;
        }
        
        .status-indicator {
            display: inline-block;
            width: 12px;
            height: 12px;
            border-radius: 50%;
            margin-right: 8px;
            animation: pulse 2s infinite;
        }
        
        .status-online { background: #48bb78; }
        
        @keyframes pulse {
            0% { opacity: 1; }
            50% { opacity: 0.5; }
            100% { opacity: 1; }
        }
        
        .last-update {
            text-align: center;
            color: #718096;
            font-size: 0.9rem;
            margin-top: 20px;
        }
        
        /* QQ头像相关样式 */
        .player-avatar {
            width: 60px;
            height: 60px;
            border-radius: 50%;
            border: 2px solid #e2e8f0;
            margin-right: 10px;
            vertical-align: middle;
            transition: all 0.3s ease;
        }
        
        .player-avatar:hover {
            border-color: #667eea;
            transform: scale(1.1);
        }
        
        .player-info {
            display: flex;
            align-items: center;
            margin-bottom: 8px;
        }
        
        .player-details {
            flex-grow: 1;
        }
        
        .member-avatar {
            width: 36px;
            height: 36px;
            border-radius: 50%;
            border: 1px solid #e2e8f0;
            margin-right: 6px;
            vertical-align: middle;
        }
        
        .member-info {
            display: inline-flex;
            align-items: center;
            margin-right: 10px;
            margin-bottom: 4px;
        }
        
        /* 头像加载失败时的默认样式 */
        .avatar-placeholder {
            background: linear-gradient(135deg, #667eea, #764ba2);
            color: white;
            display: inline-flex;
            align-items: center;
            justify-content: center;
            font-weight: bold;
            font-size: 12px;
        }
        
        @media (max-width: 768px) {
            .main-content {
                grid-template-columns: 1fr;
            }
            
            .stats-grid {
                grid-template-columns: repeat(2, 1fr);
            }
            
            .header h1 {
                font-size: 2rem;
            }
        }
        
        .empty-state {
            text-align: center;
            color: #718096;
            padding: 40px;
            font-style: italic;
        }
        
        .loading {
            text-align: center;
            color: #667eea;
            padding: 20px;
        }
        
        .notification {
            position: fixed;
            top: 20px;
            right: 20px;
            padding: 12px 20px;
            border-radius: 8px;
            color: white;
            font-weight: bold;
            z-index: 1000;
            transform: translateX(400px);
            transition: transform 0.3s ease;
        }
        
        .notification.show {
            transform: translateX(0);
        }
        
        .notification.success {
            background: #48bb78;
        }
        
        .notification.error {
            background: #e53e3e;
        }
    </style>
</head>
<body>
    <div class=""container"">
        <div class=""header"">
            <h1><span class=""status-indicator status-online""></span>Lobby Server Dashboard</h1>
            <p>Real-time monitoring and management interface</p>
            <div id=""adminModeIndicator"" style=""display: none;"">
                <div class=""admin-mode-indicator"">🔧 Admin Mode Active</div>
            </div>
        </div>
        
        <div class=""stats-grid"">
            <div class=""stat-card"">
                <h3>Online Players</h3>
                <div class=""stat-value"" id=""onlineCount"">-</div>
            </div>
            <div class=""stat-card"">
                <h3>Total Lobbies</h3>
                <div class=""stat-value"" id=""totalLobbies"">-</div>
            </div>
            <div class=""stat-card"">
                <h3>Public Lobbies</h3>
                <div class=""stat-value"" id=""publicLobbies"">-</div>
            </div>
            <div class=""stat-card"">
                <h3>Private Lobbies</h3>
                <div class=""stat-value"" id=""privateLobbies"">-</div>
            </div>
        </div>
        
        <div class=""main-content"">
            <div class=""section"">
                <h2>Active Lobbies</h2>
                <div id=""lobbiesList"">
                    <div class=""loading"">Loading lobbies...</div>
                </div>
            </div>
            
            <div class=""section"">
                <h2>Online Players</h2>
                <div id=""playersList"">
                    <div class=""loading"">Loading players...</div>
                </div>
            </div>
        </div>
        
        <button class=""refresh-btn"" onclick=""refreshAll()"">🔄 Refresh All Data</button>
        
        <div class=""last-update"">
            Last updated: <span id=""lastUpdate"">-</span>
        </div>
    </div>

    <script>
        let refreshInterval;
        let isAdminMode = false;
        
        // 检查是否为管理模式
        function checkAdminMode() {
            const urlParams = new URLSearchParams(window.location.search);
            isAdminMode = urlParams.get('op') === 'true';
            
            if (isAdminMode) {
                document.getElementById('adminModeIndicator').style.display = 'block';
                console.log('Admin mode activated');
            }
        }
        
        // 显示通知
        function showNotification(message, type = 'success') {
            // 移除现有通知
            const existingNotification = document.querySelector('.notification');
            if (existingNotification) {
                existingNotification.remove();
            }
            
            const notification = document.createElement('div');
            notification.className = `notification ${type}`;
            notification.textContent = message;
            document.body.appendChild(notification);
            
            // 显示通知
            setTimeout(() => {
                notification.classList.add('show');
            }, 100);
            
            // 3秒后隐藏
            setTimeout(() => {
                notification.classList.remove('show');
                setTimeout(() => {
                    notification.remove();
                }, 300);
            }, 3000);
        }
        
        // 踢出玩家
        async function kickPlayer(lobbyId, playerId, playerName) {
            if (!confirm(`确定要踢出玩家 ${playerName} (${playerId}) 吗？`)) {
                return;
            }
            
            try {
                const formData = new FormData();
                formData.append('lobbyID', lobbyId);
                formData.append('playerID', playerId);
                
                const response = await fetch('/api/admin/kick-player', {
                    method: 'POST',
                    body: formData
                });
                
                const result = await response.json();
                
                if (result.success) {
                    showNotification(`玩家 ${playerName} 已被踢出`, 'success');
                    // 刷新数据
                    await refreshAll();
                } else {
                    showNotification(`踢出玩家失败`, 'error');
                }
            } catch (error) {
                console.error('Error kicking player:', error);
                showNotification(`操作失败: ${error.message}`, 'error');
            }
        }
        
        function getEloClass(elo) {
            if (elo >= 1500) return 'elo-high';
            if (elo >= 1200) return 'elo-medium';
            return 'elo-low';
        }
        
        // 生成QQ头像URL
        function getQQAvatar(qqNumber, size = 200) {
            return `https://q1.qlogo.cn/g?b=qq&nk=${qqNumber}&s=${size}`;
        }
        
        // 创建头像元素，包含错误处理
        function createAvatarElement(qqNumber, className, size = 160) {
            const avatarUrl = getQQAvatar(qqNumber, size);
            const fallbackInitial = qqNumber.substring(qqNumber.length - 2);
            
            return `<img src='${avatarUrl}' 
                         class='${className}' 
                         alt='QQ${qqNumber}' 
                         title='QQ: ${qqNumber}'
                         onerror='this.style.display=""none""; this.nextElementSibling.style.display=""inline-flex"";'>
                    <div class='${className} avatar-placeholder' style='display: none;'>${fallbackInitial}</div>`;
        }
        
        async function fetchServerStatus() {
            try {
                const response = await fetch('/api/server/status');
                const data = await response.json();
                
                document.getElementById('onlineCount').textContent = data.onlineCount;
                document.getElementById('totalLobbies').textContent = data.totalLobbies;
                document.getElementById('publicLobbies').textContent = data.publicLobbies;
                document.getElementById('privateLobbies').textContent = data.privateLobbies;
                document.getElementById('lastUpdate').textContent = data.timestamp;
            } catch (error) {
                console.error('Failed to fetch server status:', error);
            }
        }
        
        async function fetchLobbies() {
            try {
                const response = await fetch('/api/lobbies/detailed');
                const lobbies = await response.json();
                const container = document.getElementById('lobbiesList');
                
                if (lobbies.length === 0) {
                    container.innerHTML = '<div class=""empty-state"">No active lobbies</div>';
                    return;
                }
                
                container.innerHTML = lobbies.map(lobby => `
                    <div class='lobby-item'>
                        <div class='lobby-header'>
                            <span class='lobby-id'>${lobby.id.substring(0, 8)}...</span>
                            <span class='lobby-type type-${lobby.typeName.toLowerCase()}'>${lobby.typeName}</span>
                        </div>
                        <div class='player-info'>
                            ${createAvatarElement(lobby.ownerId, 'member-avatar', 160)}
                            <span><strong>Owner:</strong> ${lobby.ownerName} (${lobby.ownerId})
                            ${isAdminMode ? `<button class='kick-btn' onclick='kickPlayer(""${lobby.id}"", ""${lobby.ownerId}"", ""${lobby.ownerName}"")'>踢出</button>` : ''}
                            </span>
                        </div>
                        <div class='member-count'>Members: ${lobby.currentMembers}/${lobby.maxMembers}</div>
                        ${lobby.members.length > 1 ? `
                        <div style='margin-top: 10px; font-size: 0.9rem;'>
                            <strong>Members:</strong><br>
                            <div style='margin-top: 8px;'>
                                ${lobby.members.map(member => `
                                    <div class='member-info'>
                                        ${createAvatarElement(member.id, 'member-avatar', 160)}
                                        <span>
                                            ${member.isOwner ? '👑 ' : ''}${member.name}
                                            ${member.stats ? `(ELO: <span class='${getEloClass(member.stats.ELO)}'>${member.stats.ELO}</span>)` : ''}
                                            ${isAdminMode && !member.isOwner ? `<button class='kick-btn' onclick='kickPlayer(""${lobby.id}"", ""${member.id}"", ""${member.name}"")'>踢出</button>` : ''}
                                        </span>
                                    </div>
                                `).join('')}
                            </div>
                        </div>
                        ` : ''}
                        ${isAdminMode ? `
                        <div class='admin-controls'>
                            <small style='color: #718096;'>大厅ID: ${lobby.id}</small>
                        </div>
                        ` : ''}
                    </div>
                `).join('');
            } catch (error) {
                console.error('Failed to fetch lobbies:', error);
                document.getElementById('lobbiesList').innerHTML = '<div class=""empty-state"">Failed to load lobbies</div>';
            }
        }
        
        async function fetchOnlinePlayers() {
            try {
                const response = await fetch('/api/players/online');
                const players = await response.json();
                const container = document.getElementById('playersList');
                
                if (players.length === 0) {
                    container.innerHTML = '<div class=""empty-state"">No players online</div>';
                    return;
                }
                
                container.innerHTML = players.map(player => `
                    <div class='player-item'>
                        <div class='player-info'>
                            ${createAvatarElement(player.id, 'player-avatar', 160)}
                            <div class='player-details'>
                                <div class='player-name'>${player.name}
                                ${isAdminMode && player.currentLobby ? `<button class='kick-btn' onclick='kickPlayerFromLobby(""${player.currentLobby}"", ""${player.id}"", ""${player.name}"")'>从大厅踢出</button>` : ''}
                                </div>
                                <div style='font-family: monospace; font-size: 0.8rem; color: #718096;'>QQ: ${player.id}</div>
                                ${player.stats ? `
                                <div class='player-stats'>
                                    W${player.stats.Win}-L${player.stats.Lose}-D${player.stats.Draw} | 
                                    ELO: <span class='${getEloClass(player.stats.ELO)}'>${player.stats.ELO}</span>
                                </div>
                                ` : '<div class=""player-stats"">No stats available</div>'}
                                ${player.currentLobby ? `<div style='font-size: 0.8rem; color: #667eea;'>In lobby: ${player.currentLobby.substring(0, 8)}...</div>` : ''}
                            </div>
                        </div>
                    </div>
                `).join('');
            } catch (error) {
                console.error('Failed to fetch players:', error);
                document.getElementById('playersList').innerHTML = '<div class=""empty-state"">Failed to load players</div>';
            }
        }
        
        // 从玩家列表踢出玩家
        async function kickPlayerFromLobby(lobbyId, playerId, playerName) {
            await kickPlayer(lobbyId, playerId, playerName);
        }
        
        async function refreshAll() {
            await Promise.all([
                fetchServerStatus(),
                fetchLobbies(),
                fetchOnlinePlayers()
            ]);
        }
        
        // 初始化
        checkAdminMode();
        refreshAll();
        
        // 自动刷新
        refreshInterval = setInterval(refreshAll, 5000);
        
        // 页面可见性变化时控制刷新
        document.addEventListener('visibilitychange', function() {
            if (document.hidden) {
                clearInterval(refreshInterval);
            } else {
                refreshAll();
                refreshInterval = setInterval(refreshAll, 5000);
            }
        });
    </script>
</body>
</html>";
        }
    }

    // 大厅管理器
    public class LobbyManager
    {
        private readonly ConcurrentDictionary<string, Lobby> _lobbies = new ConcurrentDictionary<string, Lobby>();

        private readonly ConcurrentDictionary<string, List<Dictionary<string, string>>> _pendingUpdates =
            new ConcurrentDictionary<string, List<Dictionary<string, string>>>();

        private readonly ILogger<LobbyManager> _logger;

        // 添加ID映射字典
        private readonly ConcurrentDictionary<string, string> _idMappings = new ConcurrentDictionary<string, string>();

        // 新增：跟踪玩家最后活动时间
        private readonly ConcurrentDictionary<string, DateTime> _playerLastActivity =
            new ConcurrentDictionary<string, DateTime>();

        // 新增：更新玩家活动时间
        public void UpdatePlayerActivity(string playerID)
        {
            _playerLastActivity[playerID] = DateTime.Now;
            _logger.LogDebug("Updated activity for player {PlayerID}", playerID);
        }

        // 新增：获取所有在线玩家及其最后活动时间
        public Dictionary<string, DateTime> GetAllPlayerActivities()
        {
            var onlinePlayers = new Dictionary<string, DateTime>();

            foreach (var lobby in _lobbies.Values)
            {
                foreach (var memberId in lobby.Members)
                {
                    if (_playerLastActivity.TryGetValue(memberId, out DateTime lastActivity))
                    {
                        // 如果玩家在多个大厅中，取最新的活动时间
                        if (!onlinePlayers.ContainsKey(memberId) || onlinePlayers[memberId] < lastActivity)
                        {
                            onlinePlayers[memberId] = lastActivity;
                        }
                    }
                }
            }

            return onlinePlayers;
        }

        // 新增：踢出不活跃的玩家
        public int KickInactivePlayers(TimeSpan inactivityThreshold)
        {
            int kickedCount = 0;
            var now = DateTime.Now;
            var playerActivities = GetAllPlayerActivities();

            foreach (var (playerId, lastActivity) in playerActivities)
            {
                var inactiveTime = now - lastActivity;
                if (inactiveTime > inactivityThreshold)
                {
                    // 找到该玩家所在的所有大厅并踢出
                    var playerLobbies = _lobbies.Values.Where(l => l.Members.Contains(playerId)).ToList();

                    foreach (var lobby in playerLobbies)
                    {
                        if (LeaveLobby(lobby.ID, playerId))
                        {
                            kickedCount++;
                            _logger.LogWarning(
                                "Kicked inactive player {PlayerID} from lobby {LobbyID} (inactive for {InactiveTime})",
                                playerId, lobby.ID, inactiveTime);
                        }
                    }

                    // 清除该玩家的活动记录
                    _playerLastActivity.TryRemove(playerId, out _);
                }
            }

            return kickedCount;
        }

        public List<Lobby> GetAllLobbies()
        {
            return _lobbies.Values.ToList();
        }

        public LobbyManager(ILogger<LobbyManager> logger)
        {
            _logger = logger;
        }

        // 生成唯一ID
        private string GenerateUniqueID(string ownerId)
        {
            return (long.Parse(ownerId) * 10 + 16384L + new Random().NextInt64(1, 999999999)).ToString();
            return Guid.NewGuid().ToString().Replace("-", "");
        }

        // 模拟客户端中的StringToSteamID方法转换
        private string SimulateSteamIDConversion(string id)
        {
            return id;
            if (string.IsNullOrEmpty(id))
                return id;

            // 完全匹配客户端算法
            ulong steamID = (ulong)((long)id.GetHashCode() + 76561197960265728L);
            return steamID.ToString();
        }

        // 获取真实的大厅ID
        private string GetRealLobbyID(string id)
        {
            // 先检查是否是直接的大厅ID
            if (_lobbies.ContainsKey(id))
            {
                return id;
            }

            // 检查是否是映射的ID
            if (_idMappings.TryGetValue(id, out string realID))
            {
                return realID;
            }

            // 添加处理数值ID的情况
            if (ulong.TryParse(id, out ulong _))
            {
                // 这是一个数值型ID，可能是客户端直接传递的Steam ID
                foreach (var lobby in _lobbies.Values)
                {
                    string steamIDStr = SimulateSteamIDConversion(lobby.ID);
                    if (steamIDStr == id)
                    {
                        // 找到匹配，添加到映射中以加速后续查找
                        _idMappings[id] = lobby.ID;
                        return lobby.ID;
                    }
                }
            }

            // 找不到映射，返回原ID
            return id;
        }

        // 获取玩家更新键
        private string GetPlayerUpdateKey(string lobbyID, string playerID)
        {
            return $"{lobbyID}:{playerID}";
        }

        // 创建大厅
        public Lobby CreateLobby(string ownerID, int lobbyType, int maxMembers)
        {
            string lobbyID = GenerateUniqueID(ownerID);
            var lobby = new Lobby
            {
                ID = lobbyID,
                OwnerID = ownerID,
                LobbyType = lobbyType,
                MaxMembers = maxMembers,
                Data = new ConcurrentDictionary<string, string>(),
                Members = new List<string> { ownerID }
            };

            _lobbies[lobbyID] = lobby;

            // 计算并存储steamID格式的映射
            string steamIDStr = SimulateSteamIDConversion(lobbyID);
            _idMappings[steamIDStr] = lobbyID;

            _logger.LogInformation(
                "Lobby {LobbyID} created by {OwnerID} with type {LobbyType} and max members {MaxMembers}, Steam format ID: {SteamID}",
                lobbyID, ownerID, lobbyType, maxMembers, steamIDStr);

            return lobby;
        }

        // 获取大厅
        public Lobby GetLobby(string lobbyID)
        {
            string realID = GetRealLobbyID(lobbyID);
            if (_lobbies.TryGetValue(realID, out Lobby lobby))
            {
                return lobby;
            }

            return null;
        }

        // 加入大厅
        public bool JoinLobby(string lobbyID, string playerID)
        {
            string realID = GetRealLobbyID(lobbyID);
            if (_lobbies.TryGetValue(realID, out Lobby lobby))
            {
                if (lobby.Members.Contains(playerID))
                {
                    _logger.LogWarning("Player {PlayerID} already in lobby {LobbyID}", playerID, lobbyID);
                    return true; // 已经在大厅中
                }

                if (lobby.Members.Count >= lobby.MaxMembers)
                {
                    _logger.LogWarning("Lobby {LobbyID} is full, cannot add player {PlayerID}", lobbyID, playerID);
                    return false; // 大厅已满
                }

                lobby.Members.Add(playerID);

                // 通知大厅中的其他成员有新成员加入
                foreach (var member in lobby.Members.Where(m => m != playerID))
                {
                    AddPendingUpdate(realID, member, new Dictionary<string, string>
                    {
                        { "type", "lobby_chat_update" },
                        { "m_ulSteamIDLobby", lobbyID }, // 使用原始lobbyID
                        { "m_ulSteamIDUserChanged", playerID },
                        { "m_ulSteamIDMakingChange", playerID },
                        { "m_rgfChatMemberStateChange", "1" } // 成员进入
                    });
                }

                _logger.LogInformation("Player {PlayerID} joined lobby {LobbyID}, current members: {MemberCount}",
                    playerID, lobbyID, lobby.Members.Count);
                return true;
            }

            _logger.LogWarning("Lobby {LobbyID} not found when player {PlayerID} tried to join", lobbyID, playerID);
            return false;
        }

        // 离开大厅
        public bool LeaveLobby(string lobbyID, string playerID)
        {
            string realID = GetRealLobbyID(lobbyID);
            if (_lobbies.TryGetValue(realID, out Lobby lobby))
            {
                if (!lobby.Members.Contains(playerID))
                {
                    _logger.LogWarning("Player {PlayerID} not in lobby {LobbyID} when trying to leave", playerID,
                        lobbyID);
                    return false; // 不在大厅中
                }

                lobby.Members.Remove(playerID);

                // 通知大厅中的其他成员有成员离开
                foreach (var member in lobby.Members)
                {
                    AddPendingUpdate(realID, member, new Dictionary<string, string>
                    {
                        { "type", "lobby_chat_update" },
                        { "m_ulSteamIDLobby", lobbyID }, // 使用原始lobbyID
                        { "m_ulSteamIDUserChanged", playerID },
                        { "m_ulSteamIDMakingChange", playerID },
                        { "m_rgfChatMemberStateChange", "4" } // 成员离开
                    });
                }

                // 如果大厅为空，则移除大厅
                if (lobby.Members.Count == 0)
                {
                    _lobbies.TryRemove(realID, out _);

                    // 同时移除ID映射
                    foreach (var mapping in _idMappings.Where(kvp => kvp.Value == realID).ToList())
                    {
                        _idMappings.TryRemove(mapping.Key, out _);
                    }

                    _logger.LogInformation("Lobby {LobbyID} removed as it has no members left", lobbyID);
                }
                // 如果拥有者离开，则选择新的拥有者
                else if (playerID == lobby.OwnerID && lobby.Members.Count > 0)
                {
                    lobby.OwnerID = lobby.Members[0];
                    _logger.LogInformation(
                        "New owner {NewOwnerID} assigned to lobby {LobbyID} after previous owner left",
                        lobby.OwnerID, lobbyID);
                }

                _logger.LogInformation("Player {PlayerID} left lobby {LobbyID}, current members: {MemberCount}",
                    playerID, lobbyID, lobby.Members.Count);
                return true;
            }

            _logger.LogWarning("Lobby {LobbyID} not found when player {PlayerID} tried to leave", lobbyID, playerID);
            return false;
        }

        // 获取大厅列表
        // 完整的 GetLobbyList 方法
        public List<string> GetLobbyList(Dictionary<string, string> filters)
        {
            var result = new List<string>();

            // 输出请求的所有过滤条件
            _logger.LogInformation("===== 大厅搜索请求 =====");
            if (filters == null)
            {
                _logger.LogWarning("未提供过滤参数，将返回空列表");
                return result;
            }

            _logger.LogInformation("收到过滤条件 {Count} 个:", filters.Count);
            foreach (var filter in filters)
            {
                _logger.LogInformation("  {Key} = {Value}", filter.Key, filter.Value);
            }

            // 解析过滤器
            List<(string key, string value, int compType)> stringFilters = new List<(string, string, int)>();
            List<(string key, int value, int compType)> numFilters = new List<(string, int, int)>();
            List<(string key, int value)> nearFilters = new List<(string, int)>();

            // 获取请求玩家ID，用于后续排序和过滤
            string requestingPlayerID = null;
            if (filters.TryGetValue("playerID", out string playerID))
            {
                requestingPlayerID = playerID;
                _logger.LogInformation("请求玩家ID: {PlayerID}", requestingPlayerID);
            }

            // 提取最大结果数
            int maxResults = 50; // 默认最大结果数
            if (filters.TryGetValue("maxResults", out string maxResultsStr) &&
                int.TryParse(maxResultsStr, out int parsedMaxResults))
            {
                maxResults = parsedMaxResults;
            }

            _logger.LogInformation("最大返回结果数: {MaxResults}", maxResults);

            // 解析字符串过滤器
            foreach (var pair in filters)
            {
                string key = pair.Key;
                string value = pair.Value;

                // 处理字符串过滤器
                if (key.StartsWith("strFilter_"))
                {
                    string filterKey = key.Substring("strFilter_".Length);
                    if (filters.TryGetValue("strFilterComp_" + filterKey, out string compTypeStr) &&
                        int.TryParse(compTypeStr, out int compType))
                    {
                        stringFilters.Add((filterKey, value, compType));
                        _logger.LogInformation("添加字符串过滤: {Key} {Op} '{Value}'",
                            filterKey, GetComparisonOperator(compType, true), value);
                    }
                }
                // 处理数值过滤器
                else if (key.StartsWith("numFilter_"))
                {
                    string filterKey = key.Substring("numFilter_".Length);
                    if (filters.TryGetValue("numFilterComp_" + filterKey, out string compTypeStr) &&
                        int.TryParse(compTypeStr, out int compType) &&
                        int.TryParse(value, out int numValue))
                    {
                        numFilters.Add((filterKey, numValue, compType));
                        _logger.LogInformation("添加数值过滤: {Key} {Op} {Value}",
                            filterKey, GetComparisonOperator(compType, false), numValue);
                    }
                }
                // 处理近似值过滤器
                else if (key.StartsWith("nearFilter_"))
                {
                    string filterKey = key.Substring("nearFilter_".Length);
                    if (int.TryParse(value, out int numValue))
                    {
                        nearFilters.Add((filterKey, numValue));
                        _logger.LogInformation("添加近似值过滤: {Key} 接近 {Value}", filterKey, numValue);
                    }
                }
            }

            // 过滤前记录所有大厅的状态
            _logger.LogInformation("开始过滤前的大厅总数: {Count}", _lobbies.Count);
            foreach (var lobby in _lobbies.Values)
            {
                _logger.LogInformation("大厅 {ID}, 类型: {Type}, 拥有者: {Owner}, 成员数: {Count}, 是否自己的大厅: {IsOwn}",
                    lobby.ID,
                    lobby.LobbyType,
                    lobby.OwnerID,
                    lobby.Members.Count,
                    lobby.OwnerID == requestingPlayerID ? "是" : "否");
            }

            // 预过滤 - 移除空大厅或无效状态的大厅
            var preFilteredLobbies = _lobbies.Values.Where(lobby =>
                lobby.Members.Count > 0 && !lobby.IsLocked).ToList();

            _logger.LogInformation("预过滤后的大厅数量: {Count} (移除了空大厅和锁定的大厅)",
                preFilteredLobbies.Count);

            // 应用过滤器
            var filteredLobbies = new List<Lobby>();
            foreach (var lobby in preFilteredLobbies)
            {
                // 默认显示所有非私有大厅，私有大厅只对拥有者可见
                if (lobby.LobbyType == 0 && requestingPlayerID != lobby.OwnerID) // 0=私有
                {
                    _logger.LogInformation("大厅 {ID} 被过滤掉: 私有大厅且请求者不是拥有者", lobby.ID);
                    continue;
                }

                bool meetsAllCriteria = true;
                string failReason = "";

                // 应用字符串过滤器
                foreach (var (key, value, compType) in stringFilters)
                {
                    if (!lobby.Data.TryGetValue(key, out string lobbyValue))
                    {
                        meetsAllCriteria = false;
                        failReason = $"缺少字符串键 {key}";
                        break;
                    }

                    bool passes = false;
                    switch (compType)
                    {
                        case 0: // Equal
                            passes = lobbyValue == value;
                            break;
                        case 1: // NotEqual
                            passes = lobbyValue != value;
                            break;
                        case 2: // Contains
                            passes = lobbyValue.Contains(value);
                            break;
                        case 3: // NotContains
                            passes = !lobbyValue.Contains(value);
                            break;
                    }

                    if (!passes)
                    {
                        meetsAllCriteria = false;
                        failReason =
                            $"字符串过滤条件不匹配: {key} {GetComparisonOperator(compType, true)} '{value}', 实际值: '{lobbyValue}'";
                        break;
                    }
                }

                if (!meetsAllCriteria)
                {
                    _logger.LogInformation("大厅 {ID} 被过滤掉: {Reason}", lobby.ID, failReason);
                    continue;
                }

                // 应用数值过滤器
                foreach (var (key, value, compType) in numFilters)
                {
                    if (!lobby.Data.TryGetValue(key, out string lobbyValueStr) ||
                        !int.TryParse(lobbyValueStr, out int lobbyValue))
                    {
                        meetsAllCriteria = false;
                        failReason = $"缺少数值键 {key} 或转换失败";
                        break;
                    }

                    bool passes = false;
                    switch (compType)
                    {
                        case 0: // Equal
                            passes = lobbyValue == value;
                            break;
                        case 1: // NotEqual
                            passes = lobbyValue != value;
                            break;
                        case 2: // GreaterThan
                            passes = lobbyValue > value;
                            break;
                        case 3: // GreaterThanOrEqual
                            passes = lobbyValue >= value;
                            break;
                        case 4: // LessThan
                            passes = lobbyValue < value;
                            break;
                        case 5: // LessThanOrEqual
                            passes = lobbyValue <= value;
                            break;
                    }

                    if (!passes)
                    {
                        meetsAllCriteria = false;
                        failReason =
                            $"数值过滤条件不匹配: {key} {GetComparisonOperator(compType, false)} {value}, 实际值: {lobbyValue}";
                        break;
                    }
                }

                if (!meetsAllCriteria)
                {
                    _logger.LogInformation("大厅 {ID} 被过滤掉: {Reason}", lobby.ID, failReason);
                    continue;
                }

                filteredLobbies.Add(lobby);
            }

            _logger.LogInformation("应用所有过滤条件后，符合条件的大厅数量: {Count}", filteredLobbies.Count);

            // 重要：先按拥有者排序，确保非请求玩家的大厅排在前面
            if (requestingPlayerID != null)
            {
                filteredLobbies.Sort((a, b) =>
                {
                    // 优先级1: 自己的大厅排后面，别人的大厅排前面
                    bool aIsOwn = (a.OwnerID == requestingPlayerID);
                    bool bIsOwn = (b.OwnerID == requestingPlayerID);

                    if (aIsOwn && !bIsOwn) return 1; // a排后面
                    if (!aIsOwn && bIsOwn) return -1; // a排前面

                    // 优先级2: 如果都是别人的大厅或都是自己的大厅，根据接近程度排序
                    return 0; // 保持原有顺序，后续会根据近似值排序
                });

                _logger.LogInformation("已对大厅进行初步排序，让非玩家{PlayerID}拥有的大厅排在前面", requestingPlayerID);
            }

            // 如果有接近值过滤器，根据接近程度排序
            if (nearFilters.Count > 0 && filteredLobbies.Count > 0)
            {
                filteredLobbies.Sort((a, b) =>
                {
                    // 如果一个是自己的大厅，一个是别人的大厅，保持之前的排序
                    if (requestingPlayerID != null)
                    {
                        bool aIsOwn = (a.OwnerID == requestingPlayerID);
                        bool bIsOwn = (b.OwnerID == requestingPlayerID);
                        if (aIsOwn != bIsOwn) return aIsOwn ? 1 : -1;
                    }

                    // 否则按接近程度排序
                    int distanceA = 0;
                    int distanceB = 0;

                    foreach (var (key, targetValue) in nearFilters)
                    {
                        if (a.Data.TryGetValue(key, out string valueStrA) &&
                            int.TryParse(valueStrA, out int valueA))
                        {
                            distanceA += Math.Abs(valueA - targetValue);
                        }
                        else
                        {
                            distanceA += 10000; // 大惩罚
                        }

                        if (b.Data.TryGetValue(key, out string valueStrB) &&
                            int.TryParse(valueStrB, out int valueB))
                        {
                            distanceB += Math.Abs(valueB - targetValue);
                        }
                        else
                        {
                            distanceB += 10000; // 大惩罚
                        }
                    }

                    _logger.LogDebug("比较大厅 {IDa} 和 {IDb} 的距离: {DistA} vs {DistB}",
                        a.ID, b.ID, distanceA, distanceB);

                    return distanceA.CompareTo(distanceB);
                });

                _logger.LogInformation("已根据近似值完成最终排序");
            }

            // 将结果转换为Steam格式的ID
            foreach (var lobby in filteredLobbies)
            {
                // 返回Steam格式的ID，因为客户端期望这种格式
                string steamIDStr = SimulateSteamIDConversion(lobby.ID);
                result.Add(steamIDStr);

                // 确保我们维护着双向映射
                _idMappings[steamIDStr] = lobby.ID;

                // 记录返回的每个大厅信息
                _logger.LogInformation("返回大厅: ID={ID}, 拥有者={Owner}, 类型={Type}, 是自己的={IsOwn}",
                    steamIDStr, lobby.OwnerID, lobby.LobbyType,
                    lobby.OwnerID == requestingPlayerID ? "是" : "否");

                // 如果达到最大结果数，则停止
                if (result.Count >= maxResults)
                {
                    _logger.LogInformation("已达到最大返回数量限制 {Max}", maxResults);
                    break;
                }
            }

            // 记录最终结果
            _logger.LogInformation("大厅搜索完成，符合条件的大厅: {Count}/{Total}",
                result.Count, _lobbies.Count);
            if (result.Count > 0)
            {
                string firstLobbyID = result[0];
                string firstLobbyOwner = "未知";

                // 尝试查找第一个大厅的拥有者
                foreach (var lobby in _lobbies.Values)
                {
                    if (SimulateSteamIDConversion(lobby.ID) == firstLobbyID)
                    {
                        firstLobbyOwner = lobby.OwnerID;
                        break;
                    }
                }

                _logger.LogInformation("第一个返回的大厅: ID={ID}, 拥有者={Owner}, 是自己的={IsOwn}",
                    firstLobbyID, firstLobbyOwner,
                    firstLobbyOwner == requestingPlayerID ? "是" : "否");
            }

            _logger.LogInformation("===== 大厅搜索结束 =====");

            return result;
        }

        // 辅助方法：获取比较运算符的字符串表示
        private string GetComparisonOperator(int compType, bool isStringComp)
        {
            if (isStringComp)
            {
                switch (compType)
                {
                    case 0: return "=="; // Equal
                    case 1: return "!="; // NotEqual
                    case 2: return "包含"; // Contains
                    case 3: return "不包含"; // NotContains
                    default: return "未知";
                }
            }
            else
            {
                switch (compType)
                {
                    case 0: return "=="; // Equal
                    case 1: return "!="; // NotEqual
                    case 2: return ">"; // GreaterThan
                    case 3: return ">="; // GreaterThanOrEqual
                    case 4: return "<"; // LessThan
                    case 5: return "<="; // LessThanOrEqual
                    default: return "未知";
                }
            }
        }

        // 获取大厅数据
        public string GetLobbyData(string lobbyID, string key)
        {
            string realID = GetRealLobbyID(lobbyID);
            if (_lobbies.TryGetValue(realID, out Lobby lobby) &&
                lobby.Data.TryGetValue(key, out string value))
            {
                return value;
            }

            return null;
        }

        // 设置大厅数据
        public bool SetLobbyData(string lobbyID, string key, string value, string playerID)
        {
            string realID = GetRealLobbyID(lobbyID);
            if (_lobbies.TryGetValue(realID, out Lobby lobby))
            {
                // 只有大厅拥有者可以修改数据
                if (lobby.OwnerID != playerID)
                {
                    _logger.LogWarning("Player {PlayerID} tried to set data for lobby {LobbyID} but is not the owner",
                        playerID, lobbyID);
                    return false;
                }

                lobby.Data[key] = value;

                // 通知其他成员数据已更新
                foreach (var member in lobby.Members.Where(m => m != playerID))
                {
                    AddPendingUpdate(realID, member, new Dictionary<string, string>
                    {
                        { "type", "lobby_data_update" },
                        { "m_ulSteamIDLobby", lobbyID }, // 使用原始lobbyID
                        { "m_ulSteamIDMember", playerID },
                        { "m_bSuccess", "1" }
                    });
                }

                _logger.LogInformation("Data set for lobby {LobbyID}, key: {Key}, value: {Value}", lobbyID, key, value);
                return true;
            }

            _logger.LogWarning("Lobby {LobbyID} not found when trying to set data", lobbyID);
            return false;
        }

        // 设置大厅类型
        public bool SetLobbyType(string lobbyID, int lobbyType, string playerID)
        {
            string realID = GetRealLobbyID(lobbyID);
            if (_lobbies.TryGetValue(realID, out Lobby lobby))
            {
                // 只有大厅拥有者可以修改类型
                if (lobby.OwnerID != playerID)
                {
                    _logger.LogWarning("Player {PlayerID} tried to set type for lobby {LobbyID} but is not the owner",
                        playerID, lobbyID);
                    return false;
                }

                lobby.LobbyType = lobbyType;
                _logger.LogInformation("Type set for lobby {LobbyID} to {LobbyType}", lobbyID, lobbyType);
                return true;
            }

            _logger.LogWarning("Lobby {LobbyID} not found when trying to set type", lobbyID);
            return false;
        }

        // 获取大厅成员数量
        public int GetLobbyMemberCount(string lobbyID)
        {
            string realID = GetRealLobbyID(lobbyID);
            if (_lobbies.TryGetValue(realID, out Lobby lobby))
            {
                return lobby.Members.Count;
            }

            return 0;
        }

        // 按索引获取大厅成员
        public string GetLobbyMemberByIndex(string lobbyID, int memberIndex)
        {
            string realID = GetRealLobbyID(lobbyID);
            if (_lobbies.TryGetValue(realID, out Lobby lobby) &&
                memberIndex >= 0 && memberIndex < lobby.Members.Count)
            {
                return lobby.Members[memberIndex];
            }

            return null;
        }

        // 获取大厅拥有者
        public string GetLobbyOwner(string lobbyID)
        {
            string realID = GetRealLobbyID(lobbyID);
            if (_lobbies.TryGetValue(realID, out Lobby lobby))
            {
                return lobby.OwnerID;
            }

            return null;
        }

        // 邀请到大厅
        public bool InviteToLobby(string lobbyID, string inviterID, string inviteeID)
        {
            string realID = GetRealLobbyID(lobbyID);
            if (_lobbies.TryGetValue(realID, out Lobby lobby))
            {
                // 检查邀请人是否在大厅中
                if (!lobby.Members.Contains(inviterID))
                {
                    _logger.LogWarning("Player {InviterID} tried to invite to lobby {LobbyID} but is not a member",
                        inviterID, lobbyID);
                    return false;
                }

                // 添加邀请通知
                AddPendingUpdate(realID, inviteeID, new Dictionary<string, string>
                {
                    { "type", "lobby_invite" },
                    { "m_ulSteamIDUser", inviterID },
                    { "m_ulSteamIDLobby", lobbyID }, // 使用原始lobbyID
                    { "m_ulGameID", "0" }
                });

                _logger.LogInformation("Player {InviterID} invited {InviteeID} to lobby {LobbyID}",
                    inviterID, inviteeID, lobbyID);
                return true;
            }

            _logger.LogWarning("Lobby {LobbyID} not found when trying to invite", lobbyID);
            return false;
        }

        // 添加待处理更新
        private void AddPendingUpdate(string lobbyID, string playerID, Dictionary<string, string> update)
        {
            string key = GetPlayerUpdateKey(lobbyID, playerID);
            var updates = _pendingUpdates.GetOrAdd(key, _ => new List<Dictionary<string, string>>());

            lock (updates)
            {
                updates.Add(update);
            }
        }

        // 获取待处理更新
        public List<Dictionary<string, string>> GetPendingUpdates(string lobbyID, string playerID)
        {
            string realID = GetRealLobbyID(lobbyID);
            string key = GetPlayerUpdateKey(realID, playerID);
            if (_pendingUpdates.TryGetValue(key, out var updates))
            {
                lock (updates)
                {
                    return updates.ToList();
                }
            }

            return new List<Dictionary<string, string>>();
        }

        // 确认更新已处理
        public void AcknowledgeUpdate(string lobbyID, string playerID, Dictionary<string, string> update)
        {
            string realID = GetRealLobbyID(lobbyID);
            string key = GetPlayerUpdateKey(realID, playerID);
            if (_pendingUpdates.TryGetValue(key, out var updates))
            {
                lock (updates)
                {
                    updates.Remove(update);
                }
            }
        }
    }

    // 添加到服务端项目
    public class PlayerStatsManager
    {
        private readonly ConcurrentDictionary<string, PlayerStats> _playerStats =
            new ConcurrentDictionary<string, PlayerStats>();

        private readonly ILogger<PlayerStatsManager> _logger;
        private readonly string _dataFilePath = "Data/player_stats.json";
        private readonly object _fileLock = new object();
        private readonly Timer _autoSaveTimer;
        private readonly TimeSpan _autoSaveInterval = TimeSpan.FromMinutes(5);

        public PlayerStatsManager(ILogger<PlayerStatsManager> logger)
        {
            _logger = logger;

            // 确保目录存在
            Directory.CreateDirectory(Path.GetDirectoryName(_dataFilePath));

            LoadPlayerStats();

            // 设置自动保存定时器
            _autoSaveTimer = new Timer(_ => SavePlayerStats(), null, _autoSaveInterval, _autoSaveInterval);
        }

        private void LoadPlayerStats()
        {
            try
            {
                if (File.Exists(_dataFilePath))
                {
                    lock (_fileLock)
                    {
                        string json = File.ReadAllText(_dataFilePath);
                        var stats = JsonConvert.DeserializeObject<Dictionary<string, PlayerStats>>(json);
                        foreach (var pair in stats)
                        {
                            _playerStats[pair.Key] = pair.Value;
                        }
                    }

                    _logger.LogInformation("Loaded {Count} player stats records from storage", _playerStats.Count);
                }
                else
                {
                    _logger.LogInformation("No player stats file found, starting with empty collection");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load player stats from {Path}", _dataFilePath);
            }
        }

        private void SavePlayerStats()
        {
            try
            {
                lock (_fileLock)
                {
                    string json = JsonConvert.SerializeObject(_playerStats, Formatting.Indented);
                    File.WriteAllText(_dataFilePath, json);
                }

                _logger.LogDebug("Saved {Count} player stats records to storage", _playerStats.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save player stats to {Path}", _dataFilePath);
            }
        }

        public PlayerStats GetPlayerStats(string playerID)
        {
            if (_playerStats.TryGetValue(playerID, out PlayerStats stats))
            {
                return stats;
            }

            return null;
        }

        public bool UpdatePlayerStats(string playerID, int win, int lose, int draw, int elo)
        {
            var stats = new PlayerStats
            {
                Win = win,
                Lose = lose,
                Draw = draw,
                ELO = elo
            };

            _playerStats[playerID] = stats;
            _logger.LogInformation("Player {PlayerID} stats updated: W{Win}-L{Lose}-D{Draw}, ELO:{ELO}",
                playerID, win, lose, draw, elo);

            // 保存更新后的数据但不要每次都保存，依靠定时保存减少I/O操作
            // 对于重要更新可以立即保存
            if (win > 0 || lose > 0 || draw > 0) // 只有在实际有比赛结果变化时才立即保存
            {
                SavePlayerStats();
            }

            return true;
        }

        // 增量更新玩家统计，适用于单场游戏结束后
        public bool IncrementPlayerStats(string playerID, bool isWin, bool isDraw, int eloChange)
        {
            // 获取现有统计或创建新统计
            var stats = _playerStats.GetOrAdd(playerID, _ => new PlayerStats());

            // 更新统计
            if (isWin) stats.Win++;
            else if (isDraw) stats.Draw++;
            else stats.Lose++;

            stats.ELO += eloChange;

            _logger.LogInformation(
                "Player {PlayerID} stats incremented: {Result}, ELO change: {EloChange}, new total: W{Win}-L{Lose}-D{Draw}, ELO:{ELO}",
                playerID, isWin ? "Win" : (isDraw ? "Draw" : "Lose"), eloChange, stats.Win, stats.Lose, stats.Draw,
                stats.ELO);

            // 游戏结果变化是重要更新，立即保存
            SavePlayerStats();

            return true;
        }

        // 应用程序关闭时确保保存数据
        public void Dispose()
        {
            _autoSaveTimer?.Dispose();
            SavePlayerStats();
        }
    }

    public class PlayerNameManager
    {
        private readonly ConcurrentDictionary<string, string> _playerNames = new ConcurrentDictionary<string, string>();
        private readonly ILogger<PlayerNameManager> _logger;
        private readonly string _dataFilePath = "Data/player_names.json";
        private readonly object _fileLock = new object();

        public PlayerNameManager(ILogger<PlayerNameManager> logger)
        {
            _logger = logger;

            // 确保目录存在
            Directory.CreateDirectory(Path.GetDirectoryName(_dataFilePath));

            LoadPlayerNames();
        }

        private void LoadPlayerNames()
        {
            try
            {
                if (File.Exists(_dataFilePath))
                {
                    lock (_fileLock)
                    {
                        string json = File.ReadAllText(_dataFilePath);
                        var names = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                        foreach (var pair in names)
                        {
                            _playerNames[pair.Key] = pair.Value;
                        }
                    }

                    _logger.LogInformation("Loaded {Count} player names from storage", _playerNames.Count);
                }
                else
                {
                    _logger.LogInformation("No player names file found, starting with empty collection");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load player names from {Path}", _dataFilePath);
            }
        }

        private void SavePlayerNames()
        {
            try
            {
                lock (_fileLock)
                {
                    string json = JsonConvert.SerializeObject(_playerNames, Formatting.Indented);
                    File.WriteAllText(_dataFilePath, json);
                }

                _logger.LogDebug("Saved {Count} player names to storage", _playerNames.Count);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to save player names to {Path}", _dataFilePath);
            }
        }

        public string GetPlayerName(string playerID)
        {
            if (_playerNames.TryGetValue(playerID, out string name))
            {
                return name;
            }

            return null;
        }

        public bool UpdatePlayerName(string playerID, string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                _logger.LogWarning("Attempted to update player {PlayerID} with empty name", playerID);
                return false;
            }

            _playerNames[playerID] = name;
            _logger.LogInformation("Player {PlayerID} name updated to: {Name}", playerID, name);

            // 保存更新后的数据
            SavePlayerNames();

            return true;
        }

        // 批量获取玩家名称的便捷方法
        public Dictionary<string, string> GetPlayerNames(IEnumerable<string> playerIDs)
        {
            var result = new Dictionary<string, string>();
            foreach (var id in playerIDs)
            {
                if (_playerNames.TryGetValue(id, out string name))
                {
                    result[id] = name;
                }
            }

            return result;
        }
    }

    // 大厅类
    public class Lobby
    {
        public string ID { get; set; }
        public string OwnerID { get; set; }
        public int LobbyType { get; set; } // 0=私有, 1=朋友, 2=公开
        public int MaxMembers { get; set; }
        public bool IsLocked { get; set; }
        public ConcurrentDictionary<string, string> Data { get; set; }
        public List<string> Members { get; set; }
    }

    public class PlayerStats
    {
        public int Win { get; set; }
        public int Lose { get; set; }
        public int Draw { get; set; }
        public int ELO { get; set; }

        [JsonIgnore] // 不序列化这些计算属性
        public int TotalGames => Win + Lose + Draw;

        [JsonIgnore] public double WinRate => TotalGames > 0 ? (double)Win / TotalGames : 0;
    }


    public class LobbyStatisticsService : BackgroundService
    {
        private readonly ILogger<LobbyStatisticsService> _logger;
        private readonly LobbyManager _lobbyManager;
        private readonly IServiceProvider _serviceProvider;
        private readonly TimeSpan _interval = TimeSpan.FromSeconds(5);

        public LobbyStatisticsService(ILogger<LobbyStatisticsService> logger, LobbyManager lobbyManager,
            IServiceProvider serviceProvider)
        {
            _logger = logger;
            _lobbyManager = lobbyManager;
            _serviceProvider = serviceProvider; // 添加这一行
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                PrintLobbyStatistics();
                await Task.Delay(_interval, stoppingToken);
            }
        }

        // 修改LobbyStatisticsService的PrintLobbyStatistics方法，添加玩家名称信息
        private void PrintLobbyStatistics()
        {
            var lobbies = _lobbyManager.GetAllLobbies();
            var playerNameManager = _serviceProvider.GetRequiredService<PlayerNameManager>();
            var playerStatsManager = _serviceProvider.GetRequiredService<PlayerStatsManager>();

            _logger.LogInformation("=== LOBBY STATISTICS ===");
            _logger.LogInformation("Total lobbies: {LobbyCount}", lobbies.Count);

            foreach (var lobby in lobbies)
            {
                // 获取并显示大厅拥有者名称
                string ownerName = playerNameManager.GetPlayerName(lobby.OwnerID) ?? "Unknown";
                var ownerStats = playerStatsManager.GetPlayerStats(lobby.OwnerID);
                string ownerElo = ownerStats != null ? $" ELO:{ownerStats.ELO}" : "";

                _logger.LogInformation(
                    "Lobby ID: {LobbyID}, Type: {LobbyType}, Owner: {OwnerID} ({OwnerName}{OwnerElo}), Members: {MemberCount}",
                    lobby.ID,
                    lobby.LobbyType,
                    lobby.OwnerID,
                    ownerName,
                    ownerElo,
                    lobby.Members.Count);

                // 显示成员信息，包括名称和统计数据
                List<string> memberDisplays = new List<string>();
                foreach (var memberID in lobby.Members)
                {
                    string memberName = playerNameManager.GetPlayerName(memberID) ?? "Unknown";
                    var memberStats = playerStatsManager.GetPlayerStats(memberID);
                    string memberDetails = memberID == lobby.OwnerID ? "(Owner)" : "";

                    if (memberStats != null)
                    {
                        memberDetails +=
                            $" W{memberStats.Win}-L{memberStats.Lose}-D{memberStats.Draw} ELO:{memberStats.ELO}";
                    }

                    memberDisplays.Add($"{memberID} ({memberName}) {memberDetails}");
                }

                _logger.LogInformation("   Members: {Members}",
                    string.Join(", ", memberDisplays));
            }

            _logger.LogInformation("=========================");
        }
    }

    public class ApplicationLifetimeService : IHostedService
    {
        private readonly PlayerStatsManager _playerStatsManager;
        private readonly IHostApplicationLifetime _appLifetime;
        private readonly ILogger<ApplicationLifetimeService> _logger;

        public ApplicationLifetimeService(
            PlayerStatsManager playerStatsManager,
            IHostApplicationLifetime appLifetime,
            ILogger<ApplicationLifetimeService> logger)
        {
            _playerStatsManager = playerStatsManager;
            _appLifetime = appLifetime;
            _logger = logger;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            _appLifetime.ApplicationStopping.Register(OnShutdown);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        private void OnShutdown()
        {
            _logger.LogInformation("Application is shutting down, ensuring all player data is saved");

            // PlayerStatsManager 在其 Dispose 方法中会保存数据
            if (_playerStatsManager is IDisposable disposable)
            {
                disposable.Dispose();
            }
        }
    }

    // 新增：玩家活动监控服务
    public class PlayerActivityMonitorService : BackgroundService
    {
        private readonly ILogger<PlayerActivityMonitorService> _logger;
        private readonly LobbyManager _lobbyManager;
        private readonly TimeSpan _checkInterval = TimeSpan.FromSeconds(3);
        private readonly TimeSpan _inactivityThreshold = TimeSpan.FromSeconds(15);

        public PlayerActivityMonitorService(
            ILogger<PlayerActivityMonitorService> logger,
            LobbyManager lobbyManager)
        {
            _logger = logger;
            _lobbyManager = lobbyManager;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation(
                "Player Activity Monitor Service started. Check interval: {Interval}, Inactivity threshold: {Threshold}",
                _checkInterval, _inactivityThreshold);

            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(_checkInterval, stoppingToken);

                    if (stoppingToken.IsCancellationRequested)
                        break;

                    CheckAndKickInactivePlayers();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error occurred while checking inactive players");
                }
            }

            _logger.LogInformation("Player Activity Monitor Service stopped");
        }

        private void CheckAndKickInactivePlayers()
        {
            var kickedCount = _lobbyManager.KickInactivePlayers(_inactivityThreshold);

            if (kickedCount > 0)
            {
                _logger.LogInformation("Kicked {Count} inactive player(s) from lobbies", kickedCount);
            }
        }
    }
}