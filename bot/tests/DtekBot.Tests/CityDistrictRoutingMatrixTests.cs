using DtekBot.Api.Bot;
using DtekBot.Api.Data;
using DtekBot.Api.Grid;
using DtekBot.Api.Parsing;
using DtekBot.Api.Settings;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Requests;

namespace DtekBot.Tests;

// No host, HTTP clients, or external providers: only the real routing pipeline and isolated SQLite.
public sealed class CityDistrictRoutingMatrixTests
{
    private const long RegionChat = 71000;
    private const long OtherRegionChat = 71999;
    private const long CombinedChat = 72000;
    private const string DtekTag = "(за даними ДТЕК)";
    private const string GridTag = "(за даними Укренерго)";
    private const string GridTitle = "Регіональні екстрені відключення";
    private const string TemplateBody =
        "|region={region}|part={part}|address={address}|message={message}|popup={popup}";

    public static IEnumerable<object[]> DistrictKinds() =>
        Kyiv().Parts.SelectMany(p => new[] { PopupKind.Emergency, PopupKind.Accident }
            .Select(kind => new object[] { p.Id, kind }));

    public static IEnumerable<object[]> DistrictOrders() =>
        Kyiv().Parts.SelectMany(p => new[] { false, true }
            .Select(dtekClearsFirst => new object[] { p.Id, dtekClearsFirst }));

    public static IEnumerable<object[]> Districts() => Kyiv().Parts.Select(p => new object[] { p.Id });

    private static RegionSettings Kyiv() => RealSettings.Load().Regions.Single(r => r.Id == "kyiv");
    private static string Encode(string text) => text.Replace("&", "&amp;", StringComparison.Ordinal)
        .Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal);
    private static string AddressLabel(string id) => $"Адреса <{id}> & дім";

    [Theory]
    [MemberData(nameof(DistrictKinds))]
    public async Task DtekOnly_EveryDistrictRoutesOnlyToMatchingAndRegionwideSubscriptions(
        string districtId, PopupKind kind)
    {
        using var h = new Harness();
        var district = h.Region.Parts.Single(p => p.Id == districtId);
        var popup = kind == PopupKind.Emergency
            ? $"Екстрені відключення електроенергії. {district.Name}."
            : $"Через аварію знеструмлено {district.Name}.";

        var classified = await h.SetDtekAsync(popup);

        Assert.Equal(kind, classified.Kind);
        Assert.Equal(new[] { districtId }, classified.PartIds);
        h.AssertPersistedPopup(classified, popup);
        foreach (var (id, chat) in h.DistrictChats)
        {
            if (id == districtId)
                h.AssertOpen(chat, classified.Hash, kind, district.Name, popup, DtekTag);
            else
            {
                Assert.Empty(h.F.Deliveries(chat));
                Assert.Empty(h.F.Bot.SentTo(chat));
            }
        }
        h.AssertOpen(RegionChat, classified.Hash, kind, district.Name, popup, DtekTag);
        Assert.Empty(h.F.Bot.SentTo(OtherRegionChat));
        await h.AssertDuplicateAsync();

        await h.SetDtekAsync(null);

        h.AssertClosed(h.DistrictChats[districtId], district.Name);
        h.AssertClosed(RegionChat, "");
        Assert.Equal(2, h.F.Db.Deliveries.AsNoTracking().Count());
        await h.AssertDuplicateAsync();
    }

    [Theory]
    [MemberData(nameof(DistrictOrders))]
    public async Task GridFirst_DtekDetailPreservesOutsideRegionalAlerts_AndClearsPerApplicableSource(
        string districtId, bool dtekClearsFirst)
    {
        using var h = new Harness();
        var district = h.Region.Parts.Single(p => p.Id == districtId);
        await h.SetGridAsync(true);
        foreach (var chat in h.AllChats)
            h.AssertOpen(chat, "grid:emergency_outages", PopupKind.Emergency, "", GridTitle, GridTag);
        var originalIds = h.AllChats.ToDictionary(chat => chat,
            chat => Assert.Single(h.F.Deliveries(chat)).TelegramMessageId);
        await h.AssertDuplicateAsync();

        var popup = $"Через аварію знеструмлено {district.Name}.";
        var classified = await h.SetDtekAsync(popup);
        Assert.Equal(new[] { districtId }, classified.PartIds);
        foreach (var (id, chat) in h.DistrictChats)
        {
            if (id == districtId)
                h.AssertOpen(chat, classified.Hash, PopupKind.Accident, district.Name, popup, DtekTag);
            else
            {
                h.AssertOpen(chat, "grid:emergency_outages", PopupKind.Emergency, "", GridTitle, GridTag);
                Assert.Empty(h.Edits(chat));
                Assert.Single(h.F.Bot.SentTo(chat));
            }
        }
        h.AssertOpen(RegionChat, classified.Hash, PopupKind.Accident, district.Name, popup, DtekTag);
        Assert.Equal(originalIds[h.DistrictChats[districtId]],
            Assert.Single(h.Edits(h.DistrictChats[districtId])).MessageId);
        await h.AssertDuplicateAsync();

        if (dtekClearsFirst)
        {
            await h.SetDtekAsync(null);
            foreach (var chat in h.AllChats)
                h.AssertOpen(chat, "grid:emergency_outages", PopupKind.Emergency, "", GridTitle, GridTag);
            Assert.All(h.F.Bot.Sent, request => Assert.Null(request.ReplyParameters));
            await h.AssertDuplicateAsync();
            await h.SetGridAsync(false);
        }
        else
        {
            await h.SetGridAsync(false);
            h.AssertOpen(h.DistrictChats[districtId], classified.Hash,
                PopupKind.Accident, district.Name, popup, DtekTag);
            h.AssertOpen(RegionChat, classified.Hash, PopupKind.Accident, district.Name, popup, DtekTag);
            foreach (var (id, chat) in h.DistrictChats.Where(p => p.Key != districtId))
                h.AssertClosed(chat, h.Region.Parts.Single(p => p.Id == id).Name);
            await h.AssertDuplicateAsync();
            await h.SetDtekAsync(null);
        }

        foreach (var (id, chat) in h.DistrictChats)
        {
            h.AssertClosed(chat, h.Region.Parts.Single(p => p.Id == id).Name);
            Assert.Equal(originalIds[chat], Assert.Single(h.F.Deliveries(chat)).TelegramMessageId);
        }
        h.AssertClosed(RegionChat, "");
        Assert.Equal(h.AllChats.Count, h.F.Db.Deliveries.AsNoTracking().Count());
        Assert.Empty(h.F.Bot.SentTo(OtherRegionChat));
        await h.AssertDuplicateAsync();
    }

    [Theory]
    [MemberData(nameof(DistrictOrders))]
    public async Task DtekFirst_RegionalGridActivationMustCreateOutsideDistrictDeliveries(
        string districtId, bool dtekClearsFirst)
    {
        using var h = new Harness();
        var district = h.Region.Parts.Single(p => p.Id == districtId);
        var popup = $"Через аварію знеструмлено {district.Name}.";
        var classified = await h.SetDtekAsync(popup);
        Assert.Equal(new[] { districtId }, classified.PartIds);
        await h.AssertDuplicateAsync();

        await h.SetGridAsync(true);

        h.AssertOpen(h.DistrictChats[districtId], classified.Hash,
            PopupKind.Accident, district.Name, popup, DtekTag);
        h.AssertOpen(RegionChat, classified.Hash, PopupKind.Accident, district.Name, popup, DtekTag);
        foreach (var (id, chat) in h.DistrictChats.Where(p => p.Key != districtId))
        {
            var deliveries = h.F.Deliveries(chat);
            Assert.True(deliveries.Count == 1,
                $"DTEK-first district={districtId}; outside district={id}: expected one persisted "
                + $"Ukrenergo delivery after regional grid activation, actual={deliveries.Count}.");
            h.AssertOpen(chat, "grid:emergency_outages", PopupKind.Emergency, "", GridTitle, GridTag);
        }
        await h.AssertDuplicateAsync();
        if (dtekClearsFirst)
        {
            await h.SetDtekAsync(null);
            foreach (var chat in h.AllChats)
                h.AssertOpen(chat, "grid:emergency_outages", PopupKind.Emergency, "", GridTitle, GridTag);
            await h.AssertDuplicateAsync();
            await h.SetGridAsync(false);
        }
        else
        {
            await h.SetGridAsync(false);
            h.AssertOpen(h.DistrictChats[districtId], classified.Hash,
                PopupKind.Accident, district.Name, popup, DtekTag);
            h.AssertOpen(RegionChat, classified.Hash, PopupKind.Accident, district.Name, popup, DtekTag);
            foreach (var (id, chat) in h.DistrictChats.Where(p => p.Key != districtId))
                h.AssertClosed(chat, h.Region.Parts.Single(p => p.Id == id).Name);
            await h.AssertDuplicateAsync();
            await h.SetDtekAsync(null);
        }
        foreach (var (id, chat) in h.DistrictChats)
            h.AssertClosed(chat, h.Region.Parts.Single(p => p.Id == id).Name);
        h.AssertClosed(RegionChat, "");
        await h.AssertDuplicateAsync();
    }

    [Theory]
    [MemberData(nameof(Districts))]
    public async Task AddressScopedTemplates_UseOnlyAffectedSavedAddress_AndPersistOneThread(string districtId)
    {
        using var h = new Harness();
        var district = h.Region.Parts.Single(p => p.Id == districtId);
        var outside = h.Region.Parts.First(p => p.Id != districtId);
        h.F.AddChannel(CombinedChat, channel =>
        {
            channel.CreatedAt = h.F.Clock.Now;
            channel.PartIdList = [outside.Id];
            channel.TemplateMap = Harness.Templates();
            channel.Addresses =
            [
                new ChatAddress { RegionId = "kyiv", Name = "Дім <1>", Label = "Вулиця <1> & будинок",
                    PartIds = districtId },
                new ChatAddress { RegionId = "kyiv", Name = "Офіс", Label = "Чужа адреса",
                    PartIds = outside.Id },
            ];
        });
        h.F.Db.ChangeTracker.Clear();
        var saved = h.F.LoadChannel(CombinedChat);
        Assert.Equal(2, saved.Addresses.Count);
        Assert.Equal(Harness.Templates()[MessageSlots.Regional], saved.TemplateFor(MessageSlots.Regional));

        var popup = $"Через аварію знеструмлено {district.Name}.";
        await h.SetDtekAsync(popup);

        var request = Assert.Single(h.F.Bot.SentTo(CombinedChat));
        Assert.Contains("regional|region=м. Київ|part=" + Encode(district.Name)
            + "|address=Вулиця &lt;1&gt; &amp; будинок|", request.Text);
        Assert.Contains(Encode(popup), request.Text);
        Assert.Contains(DtekTag, request.Text);
        Assert.Contains("📍 Дім &lt;1&gt;", request.Text);
        Assert.DoesNotContain("Чужа адреса", request.Text);
        Assert.DoesNotContain("Офіс", request.Text);
        var original = Assert.Single(h.F.Deliveries(CombinedChat));
        Assert.False(original.IsClosed);
        await h.AssertDuplicateAsync();

        await h.SetDtekAsync(null);

        var ended = h.F.Bot.SentTo(CombinedChat).Last();
        Assert.Equal(original.TelegramMessageId, ended.ReplyParameters?.MessageId);
        Assert.True(Assert.Single(h.F.Deliveries(CombinedChat)).IsClosed);
        Assert.Equal(2, h.F.Bot.SentTo(CombinedChat).Count());
        await h.AssertDuplicateAsync();
    }

    private sealed class Harness : IDisposable
    {
        public DispatcherFixture F { get; } = new();
        public RegionSettings Region => F.Kyiv;
        public Dictionary<string, long> DistrictChats { get; } = new();
        public List<long> AllChats => [RegionChat, .. DistrictChats.Values];
        private readonly RegionAlertCoordinator coordinator;

        public Harness()
        {
            coordinator = new RegionAlertCoordinator(F.Db, F.Dispatcher, F.Classifier,
                F.Options, NullLogger<RegionAlertCoordinator>.Instance);
            for (var i = 0; i < Region.Parts.Count; i++)
            {
                var id = Region.Parts[i].Id;
                var chat = RegionChat + i + 1;
                DistrictChats.Add(id, chat);
                F.AddChannel(chat, channel => Configure(channel, [id], AddressLabel(id)));
            }
            F.AddChannel(RegionChat, channel => Configure(channel, [], AddressLabel("region")));
            F.AddChannel(OtherRegionChat, channel => Configure(channel, [], "Інший регіон"), "odesa");
            F.Db.ChangeTracker.Clear();
            foreach (var chat in AllChats)
                Assert.Equal(Templates()[MessageSlots.Regional], F.LoadChannel(chat).TemplateFor(MessageSlots.Regional));
        }

        public static Dictionary<string, string> Templates() => new()
        {
            [MessageSlots.Emergency] = "emergency" + TemplateBody,
            [MessageSlots.Regional] = "regional" + TemplateBody,
            [MessageSlots.Ended] = "ended" + TemplateBody,
        };

        private void Configure(Channel channel, IReadOnlyList<string> parts, string address)
        {
            channel.CreatedAt = F.Clock.Now;
            channel.PartIdList = parts;
            channel.AddressLabel = address;
            channel.TemplateMap = Templates();
        }

        public async Task<PopupClassification> SetDtekAsync(string? popup)
        {
            var result = F.Classifier.Classify("kyiv", popup);
            var state = F.Db.RegionStates.SingleOrDefault(s => s.RegionId == "kyiv");
            if (state is null)
            {
                state = new RegionState { RegionId = "kyiv" };
                F.Db.RegionStates.Add(state);
            }
            state.Kind = result.Kind;
            state.Hash = result.Hash;
            state.NormalizedText = result.NormalizedText;
            state.PopupText = popup;
            state.PartIdList = result.PartIds;
            state.ObservedAt = state.UpdatedAt = F.Clock.Now;
            await SaveAndRecomputeAsync();
            return result;
        }

        public async Task SetGridAsync(bool active)
        {
            var state = F.Db.GridStates.SingleOrDefault(s => s.RegionId == "kyiv");
            if (state is null)
            {
                state = new GridState { RegionId = "kyiv" };
                F.Db.GridStates.Add(state);
            }
            state.Status = active ? GridStatuses.EmergencyOutages : GridStatuses.Normal;
            state.Title = GridTitle;
            state.Since = F.Clock.Now.AddMinutes(-5);
            state.FeedGeneratedAt = state.UpdatedAt = F.Clock.Now;
            await SaveAndRecomputeAsync();
        }

        private async Task SaveAndRecomputeAsync()
        {
            await F.Db.SaveChangesAsync();
            F.Db.ChangeTracker.Clear();
            var summary = await coordinator.RecomputeAsync("kyiv", CancellationToken.None);
            Assert.NotNull(summary);
            Assert.Equal(0, summary.Failed);
            F.Db.ChangeTracker.Clear();
        }

        public void AssertPersistedPopup(PopupClassification result, string popup)
        {
            var state = F.Db.RegionStates.AsNoTracking().Single();
            Assert.Equal(result.Kind, state.Kind);
            Assert.Equal(result.Hash, state.Hash);
            Assert.Equal(result.PartIds, state.PartIdList);
            Assert.Equal(popup, state.PopupText);
        }

        public IEnumerable<EditMessageTextRequest> Edits(long chat) =>
            F.Bot.Edits.Where(request => request.ChatId.Identifier == chat);

        private string Latest(long chat) => Edits(chat).LastOrDefault()?.Text
            ?? F.Bot.SentTo(chat).Last().Text;

        public void AssertOpen(long chat, string hash, PopupKind kind, string part, string popup, string tag)
        {
            var delivery = Assert.Single(F.Deliveries(chat));
            Assert.False(delivery.IsClosed);
            Assert.Equal("kyiv", delivery.RegionId);
            Assert.Equal(hash, delivery.ContentHash);
            Assert.Equal(F.Clock.Now, delivery.UpdatedAt);
            Assert.True(delivery.TelegramMessageId >= 1000);
            var text = Latest(chat);
            var slot = kind == PopupKind.Emergency ? "emergency" : "regional";
            var id = chat == RegionChat ? "region" : DistrictChats.Single(p => p.Value == chat).Key;
            Assert.StartsWith(slot + "|region=" + Encode(Region.Name)
                + "|part=" + Encode(part) + "|address=" + Encode(AddressLabel(id)) + "|", text);
            Assert.Contains(tag, text);
            Assert.Contains(Encode(popup), text);
            Assert.DoesNotContain("{region}", text);
            Assert.DoesNotContain("{part}", text);
            Assert.DoesNotContain("{address}", text);
        }

        public void AssertClosed(long chat, string part)
        {
            var delivery = Assert.Single(F.Deliveries(chat));
            Assert.True(delivery.IsClosed);
            var ended = Assert.Single(F.Bot.SentTo(chat), request => request.ReplyParameters is not null);
            Assert.Equal(delivery.TelegramMessageId, ended.ReplyParameters!.MessageId);
            Assert.Contains("ended|region=" + Encode(Region.Name) + "|part=" + Encode(part) + "|", ended.Text);
            Assert.Contains("запис про екстрені відключення", ended.Text);
            Assert.Contains("Це не підтверджує", ended.Text);
        }

        public async Task AssertDuplicateAsync()
        {
            var count = F.Bot.Requests.Count;
            var before = F.Db.Deliveries.AsNoTracking().OrderBy(d => d.Id)
                .Select(d => new { d.Id, d.ChatId, d.ContentHash, d.TelegramMessageId, d.IsClosed, d.UpdatedAt }).ToList();
            var summary = await coordinator.RecomputeAsync("kyiv", CancellationToken.None);
            Assert.Equal(new DispatchSummary(0, 0, 0, 0), summary);
            Assert.Equal(count, F.Bot.Requests.Count);
            F.Db.ChangeTracker.Clear();
            var after = F.Db.Deliveries.AsNoTracking().OrderBy(d => d.Id)
                .Select(d => new { d.Id, d.ChatId, d.ContentHash, d.TelegramMessageId, d.IsClosed, d.UpdatedAt }).ToList();
            Assert.Equal(before, after);
        }

        public void Dispose() => F.Dispose();
    }
}
