using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Services;

namespace WatchPartyForEmby.Api
{
    [Route("/WatchParty/List", "GET", Summary = "Get all watch parties")]
    [Authenticated]
    public class WatchPartyListRequest : IReturn<WatchPartyListResponse>
    {
        [ApiMember(Name = "UserId", Description = "User ID to filter accessible parties", IsRequired = false)]
        public string UserId { get; set; }
        
        [ApiMember(Name = "Password", Description = "Party password for external access", IsRequired = false)]
        public string Password { get; set; }
    }

    public class WatchPartyListResponse
    {
        public List<WatchPartyInfo> Parties { get; set; }
    }

    public class WatchPartyInfo
    {
        public string Id { get; set; }
        public string ItemId { get; set; }
        public string ItemName { get; set; }
        public string ItemType { get; set; }
        public bool IsActive { get; set; }
        public bool IsWaitingRoom { get; set; }
        public int ParticipantCount { get; set; }
        public int MaxParticipants { get; set; }
        public string HostUserName { get; set; }
        public long CurrentPositionTicks { get; set; }
        public bool IsPlaying { get; set; }
        public bool RequiresPassword { get; set; }
        public bool IsSeriesParty { get; set; }
        public string SeriesName { get; set; }
        public string CurrentEpisodeId { get; set; }
        public string CurrentEpisodeName { get; set; }
        public int CurrentEpisodeIndex { get; set; }
        public int EpisodeCount { get; set; }
    }

    [Route("/WatchParty/{Id}/Participants", "GET", Summary = "Get party participants")]
    [Authenticated]
    public class PartyParticipantsRequest : IReturn<PartyParticipantsResponse>
    {
        [ApiMember(Name = "Id", Description = "Party ID", IsRequired = true)]
        public string Id { get; set; }
    }

    public class PartyParticipantsResponse
    {
        public List<ParticipantInfo> Participants { get; set; }
    }

    public class ParticipantInfo
    {
        public string UserId { get; set; }
        public string UserName { get; set; }
        public string SessionId { get; set; }
        public string Client { get; set; }
        public bool SupportsRemoteControl { get; set; }
        public bool IsHost { get; set; }
        public bool IsReady { get; set; }
        public bool IsBuffering { get; set; }
        public long CurrentPositionTicks { get; set; }
        public DateTime LastActivityAt { get; set; }
    }

    [Route("/WatchParty/{Id}/Ready", "POST", Summary = "Mark user as ready")]
    [Authenticated]
    public class SetReadyRequest : IReturnVoid
    {
        [ApiMember(Name = "Id", Description = "Party ID", IsRequired = true)]
        public string Id { get; set; }
        
        [ApiMember(Name = "UserId", Description = "Deprecated; the authenticated Emby user is used", IsRequired = false)]
        public string UserId { get; set; }
        
        [ApiMember(Name = "IsReady", Description = "Ready state", IsRequired = true)]
        public bool IsReady { get; set; }
    }

    [Route("/WatchParty/{Id}/Start", "POST", Summary = "Start party from waiting room")]
    [Authenticated]
    public class StartPartyRequest : IReturnVoid
    {
        [ApiMember(Name = "Id", Description = "Party ID", IsRequired = true)]
        public string Id { get; set; }
        
        [ApiMember(Name = "UserId", Description = "Deprecated; the authenticated Emby user is used", IsRequired = false)]
        public string UserId { get; set; }
    }

    [Route("/WatchParty/Users", "GET", Summary = "Get all Emby users")]
    [Authenticated]
    public class GetUsersRequest : IReturn<GetUsersResponse>
    {
    }

    public class GetUsersResponse
    {
        public List<EmbyUserInfo> Users { get; set; }
    }

    public class EmbyUserInfo
    {
        public string Id { get; set; }
        public string Name { get; set; }
    }

    [Route("/WatchPartyForEmby/Images/{ImageName}", "GET", Summary = "Gets a plugin image resource")]
    public class GetImageRequest : IReturn<Stream>
    {
        [ApiMember(Name = "ImageName", Description = "Image file name", IsRequired = true)]
        public string ImageName { get; set; }
    }

    public class WatchPartyService : IService, IRequiresRequest
    {
        private readonly IUserManager _userManager;
        private readonly IAuthorizationContext _authorizationContext;
        private readonly ISessionManager _sessionManager;

        public WatchPartyService(
            IUserManager userManager,
            IAuthorizationContext authorizationContext,
            ISessionManager sessionManager)
        {
            _userManager = userManager;
            _authorizationContext = authorizationContext;
            _sessionManager = sessionManager;
        }

        public IRequest Request { get; set; }

        public object Get(WatchPartyListRequest request)
        {
            var currentUser = GetAuthenticatedUser();
            var currentUserId = currentUser.Id.ToString();
            var isAdministrator = currentUser.Policy?.IsAdministrator == true;
            List<WatchPartyItem> partySnapshot;
            lock (Plugin.Instance.ConfigurationSyncRoot)
            {
                partySnapshot = Plugin.Instance.Configuration.WatchParties.ToList();
            }
            var parties = new List<WatchPartyInfo>();

            foreach (var party in partySnapshot)
            {
                if (!string.IsNullOrEmpty(party.PasswordHash))
                {
                    if (string.IsNullOrEmpty(request.Password) || !PasswordHelper.VerifyPassword(request.Password, party.PasswordHash))
                    {
                        continue;
                    }
                }

                if (!WatchPartyAuthorizationPolicy.CanAccessParty(
                        party,
                        currentUserId,
                        isAdministrator))
                {
                    continue;
                }

                string hostUserName = "Not set";
                if (!string.IsNullOrEmpty(party.HostUserId))
                {
                    var hostUser = _userManager.GetUserById(party.HostUserId);
                    if (hostUser != null)
                    {
                        hostUserName = hostUser.Name;
                    }
                }

                var currentEpisode = SeriesPartyQueue.GetCurrentEpisode(party);
                var participantCount = Plugin.Instance.PartyParticipants.DistinctUserCount(party.Id);
                
                parties.Add(new WatchPartyInfo
                {
                    Id = party.Id,
                    ItemId = party.ItemId,
                    ItemName = party.ItemName,
                    ItemType = party.ItemType,
                    IsActive = party.IsActive,
                    IsWaitingRoom = party.IsWaitingRoom,
                    ParticipantCount = participantCount,
                    MaxParticipants = party.MaxParticipants,
                    HostUserName = hostUserName,
                    CurrentPositionTicks = party.CurrentPositionTicks,
                    IsPlaying = party.IsPlaying,
                    RequiresPassword = !string.IsNullOrEmpty(party.PasswordHash),
                    IsSeriesParty = party.IsSeriesParty,
                    SeriesName = party.SeriesName,
                    CurrentEpisodeId = party.CurrentEpisodeId,
                    CurrentEpisodeName = currentEpisode?.ItemName,
                    CurrentEpisodeIndex = party.CurrentEpisodeIndex,
                    EpisodeCount = party.EpisodeQueue?.Count ?? 0
                });
            }

            return new WatchPartyListResponse { Parties = parties };
        }

        public object Get(PartyParticipantsRequest request)
        {
            var currentUser = GetAuthenticatedUser();
            var plugin = Plugin.Instance;
            var participants = new List<ParticipantInfo>();

            WatchPartyItem party;
            lock (plugin.ConfigurationSyncRoot)
            {
                party = plugin.Configuration.WatchParties.FirstOrDefault(p => p.Id == request.Id);
            }
            if (party == null)
            {
                throw new ArgumentException($"Party {request.Id} not found");
            }
            if (!WatchPartyAuthorizationPolicy.CanAccessParty(
                    party,
                    currentUser.Id.ToString(),
                    currentUser.Policy?.IsAdministrator == true))
            {
                throw new UnauthorizedAccessException("The current user cannot access this party");
            }
            var readyUsers = plugin.PartyReadyUsers.GetReadyUsersSnapshot(request.Id);
            var sessionDescriptors = _sessionManager.Sessions.Select(session =>
                new ParticipantSessionDescriptor
                {
                    SessionId = session.Id,
                    Client = session.Client,
                    SupportsRemoteControl = session.SupportsRemoteControl
                });
            participants = ParticipantInfoProjector.Project(
                plugin.PartyParticipants.GetSessions(request.Id)
                    .OrderByDescending(participant => participant.LastActivityAt),
                readyUsers,
                plugin.PartyParticipants.GetMasterSession(request.Id),
                sessionDescriptors);

            return new PartyParticipantsResponse
            {
                Participants = participants
            };
        }

        public void Post(SetReadyRequest request)
        {
            var currentUser = GetAuthenticatedUser();
            var currentUserId = currentUser.Id.ToString();
            var plugin = Plugin.Instance;
            WatchPartyItem party;
            lock (plugin.ConfigurationSyncRoot)
            {
                party = plugin.Configuration.WatchParties.FirstOrDefault(p => p.Id == request.Id);
            }
            
            if (party == null)
            {
                throw new ArgumentException($"Party {request.Id} not found");
            }

            if (!WatchPartyAuthorizationPolicy.CanSetReady(
                    party,
                    currentUserId,
                    currentUser.Policy?.IsAdministrator == true,
                    plugin.PartyParticipants.HasUser(request.Id, currentUserId)))
            {
                throw new UnauthorizedAccessException(
                    "The current user must be actively participating in this party");
            }

            plugin.PartyReadyUsers.SetReady(request.Id, currentUserId, request.IsReady);
        }

        public async Task Post(StartPartyRequest request)
        {
            var currentUser = GetAuthenticatedUser();
            var plugin = Plugin.Instance;
            WatchPartyItem party;
            lock (plugin.ConfigurationSyncRoot)
            {
                party = plugin.Configuration.WatchParties.FirstOrDefault(p => p.Id == request.Id);

                if (party == null)
                {
                    throw new ArgumentException($"Party {request.Id} not found");
                }

                if (!WatchPartyAuthorizationPolicy.CanStartParty(
                        party,
                        currentUser.Id.ToString(),
                        currentUser.Policy?.IsAdministrator == true))
                {
                    throw new UnauthorizedAccessException("Only the host can start the party");
                }
            }

            await plugin.WaitingRoomStarts.StartAsync(request.Id).ConfigureAwait(false);
        }

        public object Get(GetUsersRequest request)
        {
            var currentUser = GetAuthenticatedUser();
            if (currentUser.Policy?.IsAdministrator != true)
            {
                throw new UnauthorizedAccessException("Only administrators can list Emby users");
            }

            return new GetUsersResponse
            {
                Users = _userManager.GetUserList(new MediaBrowser.Model.Querying.UserQuery()).Select(u => new EmbyUserInfo 
                { 
                    Id = u.Id.ToString(), 
                    Name = u.Name 
                }).ToList()
            };
        }

        public Stream Get(GetImageRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.ImageName))
                {
                    return Stream.Null;
                }

                var assembly = typeof(Plugin).GetTypeInfo().Assembly;
                var resourceName = $"WatchPartyForEmby.images.{request.ImageName}";

                var stream = assembly.GetManifestResourceStream(resourceName);

                if (stream == null)
                {
                    return Stream.Null;
                }

                return stream;
            }
            catch (Exception)
            {
                return Stream.Null;
            }
        }

        private MediaBrowser.Controller.Entities.User GetAuthenticatedUser()
        {
            var authorization = _authorizationContext.GetAuthorizationInfo(Request);
            if (authorization?.User == null || authorization.UserId <= 0)
            {
                throw new UnauthorizedAccessException("Authentication is required");
            }

            return authorization.User;
        }
    }
}
