using DtekBot.Api.Bot;
using DtekBot.Api.Data;
using DtekBot.Api.Parsing;
using DtekBot.Api.Settings;
using DtekBot.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Telegram.Bot.Requests;

namespace DtekBot.Tests;

// Offline integration: real settings/classification/coordinator/dispatcher, SQLite and fake Telegram.
// State writes model accepted ingest/feed updates; no HTTP, scraper, LLM or external service is used.
public sealed class OblastDistrictRoutingMatrixTests
{
    public static IEnumerable<object[]> Districts()
    {
        foreach (var region in RealSettings.Load().Regions.Where(r => r.Id != "kyiv"))
        foreach (var part in region.Parts)
        foreach (var structured in new[] { false, true })
            yield return [region.Id, part.Id, structured];
    }

    public static IEnumerable<object[]> Clearances()
    {
        foreach (var row in Districts())
        foreach (var gridClearsFirst in new[] { false, true })
            yield return [row[0], row[1], row[2], gridClearsFirst];
    }

    public static IEnumerable<object[]> Regions() =>
        RealSettings.Load().Regions.Where(r => r.Id != "kyiv").Select(r => new object[] { r.Id });

    [Theory]
    [MemberData(nameof(Districts))]
    public async Task DtekOnly_RoutesEveryConfiguredDistrict_AndPersistsTemplatesAndClearance(
        string regionId, string partId, bool structured)
    {
        using var matrix = new Matrix(regionId, partId);
        var classification = await matrix.PopupAsync(structured);

        Assert.Equal(PopupKind.Accident, classification.Kind);
        Assert.Equal(matrix.ExpectedParts.Order(), classification.PartIds.Order());
        foreach (var chat in matrix.Matching)
        {
            matrix.AssertDtek(chat);
            Assert.False(matrix.Delivery(chat).IsClosed);
            Assert.StartsWith(classification.Hash, matrix.Delivery(chat).ContentHash);
        }
        Assert.Empty(matrix.F.Deliveries(Matrix.Outside));
        Assert.Empty(matrix.Sent(Matrix.Outside));
        matrix.AssertIsolation();

        var requestCount = matrix.F.Bot.Requests.Count;
        await matrix.RecomputeAsync();
        Assert.Equal(requestCount, matrix.F.Bot.Requests.Count);

        await matrix.ClearPopupAsync();
        foreach (var chat in matrix.Matching)
            matrix.AssertClosed(chat);
        Assert.Empty(matrix.F.Deliveries(Matrix.Outside));
        matrix.AssertIsolation();

        requestCount = matrix.F.Bot.Requests.Count;
        await matrix.RecomputeAsync();
        Assert.Equal(requestCount, matrix.F.Bot.Requests.Count);
    }

    [Theory]
    [MemberData(nameof(Clearances))]
    public async Task RegionalGridThenNamedDtek_OnlyApplicableSourcesControlEachSubscription(
        string regionId, string partId, bool structured, bool gridClearsFirst)
    {
        using var matrix = new Matrix(regionId, partId);
        await matrix.GridAsync(true);
        foreach (var chat in matrix.AllLocal)
            matrix.AssertGrid(chat);
        var originalIds = matrix.AllLocal.ToDictionary(c => c, c => matrix.Delivery(c).TelegramMessageId);

        await matrix.PopupAsync(structured);
        foreach (var chat in matrix.Matching)
        {
            matrix.AssertDtek(chat);
            Assert.Equal(originalIds[chat], matrix.Delivery(chat).TelegramMessageId);
        }
        matrix.AssertGrid(Matrix.Outside);
        Assert.Empty(matrix.Edits(Matrix.Outside));
        Assert.False(matrix.Delivery(Matrix.Outside).IsClosed);
        matrix.AssertIsolation();

        if (gridClearsFirst)
        {
            await matrix.GridAsync(false);
            matrix.AssertClosed(Matrix.Outside);
            foreach (var chat in matrix.Matching)
            {
                matrix.AssertDtek(chat);
                Assert.False(matrix.Delivery(chat).IsClosed);
                Assert.Single(matrix.Sent(chat));
            }
            await matrix.ClearPopupAsync();
        }
        else
        {
            await matrix.ClearPopupAsync();
            foreach (var chat in matrix.AllLocal)
            {
                matrix.AssertGrid(chat);
                Assert.False(matrix.Delivery(chat).IsClosed);
                Assert.Single(matrix.Sent(chat));
            }
            await matrix.GridAsync(false);
        }

        foreach (var chat in matrix.AllLocal)
        {
            matrix.AssertClosed(chat);
            Assert.Equal(originalIds[chat], matrix.Sent(chat).Last().ReplyParameters?.MessageId);
        }
        matrix.AssertIsolation();
    }

    [Theory]
    [MemberData(nameof(Districts))]
    public async Task NamedDtekThenRegionalGrid_MustAlsoOpenOutsideDistrictSubscription(
        string regionId, string partId, bool structured)
    {
        using var matrix = new Matrix(regionId, partId);
        await matrix.PopupAsync(structured);
        Assert.Empty(matrix.F.Deliveries(Matrix.Outside));

        await matrix.GridAsync(true);

        // Strict contract regression: a regional Ukrenergo emergency applies outside the DTEK scope,
        // including subscribers that did not already have an open regional delivery.
        matrix.AssertGrid(Matrix.Outside);
        Assert.False(matrix.Delivery(Matrix.Outside).IsClosed);
        foreach (var chat in matrix.Matching)
            matrix.AssertDtek(chat);
        matrix.AssertIsolation();
    }

    [Theory]
    [MemberData(nameof(Regions))]
    public async Task AcceptedStructuredAll_OverridesNamedText_AndReachesEveryLocalSubscription(string regionId)
    {
        var firstPart = RealSettings.Load().Regions.Single(r => r.Id == regionId).Parts[0].Id;
        using var matrix = new Matrix(regionId, firstPart);
        var result = await matrix.PopupAsync(true, all: true);

        Assert.Empty(result.PartIds);
        foreach (var chat in matrix.AllLocal)
        {
            var text = matrix.LatestText(chat);
            Assert.Contains("(за даними ДТЕК)", text);
            Assert.Contains("parts=[]", text);
            Assert.False(matrix.Delivery(chat).IsClosed);
        }
        matrix.AssertIsolation();

        await matrix.ClearPopupAsync();
        foreach (var chat in matrix.AllLocal)
            matrix.AssertClosed(chat);
        matrix.AssertIsolation();
    }

    private sealed class Matrix : IDisposable
    {
        public const long Inside = 201;
        public const long Outside = 202;
        public const long Regionwide = 203;
        private const string GridTag = "(за даними Укренерго)";
        private const string DtekTag = "(за даними ДТЕК)";
        private const string GridTitle = "matrix-grid-emergency";
        private readonly RegionAlertCoordinator _coordinator;
        private readonly PartSettings _part;
        private readonly List<long> _isolated = [];
        private readonly string _popup;
        private readonly string _outsideName;

        public DispatcherFixture F { get; } = new();
        public RegionSettings Region { get; }
        public List<string> ExpectedParts { get; } = [];
        public List<long> Matching { get; } = [Inside, Regionwide];
        public IEnumerable<long> AllLocal => Matching.Append(Outside);

        public Matrix(string regionId, string partId)
        {
            Region = F.Settings.Regions.Single(r => r.Id == regionId);
            _part = Region.Parts.Single(p => p.Id == partId);
            _popup = $"Через аварію знеструмлено {_part.Name}. MATRIX-DTEK-DETAIL.";
            var pending = new Queue<string>([partId]);
            while (pending.TryDequeue(out var id))
            {
                if (ExpectedParts.Contains(id)) continue;
                ExpectedParts.Add(id);
                foreach (var child in Region.Parts.Single(p => p.Id == id).Children)
                    pending.Enqueue(child);
            }

            var outside = Region.Parts.First(p => !ExpectedParts.Contains(p.Id));
            _outsideName = outside.Name;
            Add(Inside, [partId]);
            Add(Outside, [outside.Id]);
            Add(Regionwide, []);
            foreach (var child in ExpectedParts.Where(p => p != partId))
            {
                var chat = 300 + Matching.Count;
                Add(chat, [child]);
                Matching.Add(chat);
            }

            long otherChat = 400;
            foreach (var otherRegion in F.Settings.Regions.Where(r => r.Id != regionId))
            {
                var chat = otherChat++;
                _isolated.Add(chat);
                // Reusing the exact target id must never bypass the region boundary.
                Add(chat, [partId], otherRegion.Id);
            }

            _coordinator = new RegionAlertCoordinator(F.Db, F.Dispatcher, F.Classifier, F.Options,
                NullLogger<RegionAlertCoordinator>.Instance);
        }

        private void Add(long chat, string[] parts, string? region = null) =>
            F.AddChannel(chat, c =>
            {
                c.CreatedAt = F.Clock.Now;
                c.PartIdList = parts;
                c.Template = "WRONG-LEGACY-TEMPLATE";
                c.TemplateMap = new Dictionary<string, string>
                {
                    [MessageSlots.Emergency] = $"GRID-{chat}|{{message}}|region=[{{region}}]|parts=[{{part}}]|{{popup}}|{{site}}",
                    [MessageSlots.Regional] = $"DTEK-{chat}|{{message}}|region=[{{region}}]|parts=[{{part}}]|{{popup}}|{{site}}",
                    [MessageSlots.Ended] = $"ENDED-{chat}|{{message}}|region=[{{region}}]|parts=[{{part}}]",
                };
            }, region ?? Region.Id);

        public async Task<PopupClassification> PopupAsync(bool structured, bool all = false)
        {
            var parsed = structured
                ? new StructuredPopup(all ? "all" : "districts", all ? [] : [_part.Id])
                : null;
            // Structured inputs are accepted parser results, not calls to the optional extraction service.
            var classification = F.Classifier.Classify(Region.Id, _popup, parsed);
            await SavePopupAsync(_popup, classification);
            return classification;
        }

        public Task ClearPopupAsync() =>
            SavePopupAsync(null, F.Classifier.Classify(Region.Id, null));

        private async Task SavePopupAsync(string? text, PopupClassification classification)
        {
            var state = F.Db.RegionStates.SingleOrDefault(s => s.RegionId == Region.Id);
            if (state is null)
            {
                state = new RegionState { RegionId = Region.Id };
                F.Db.RegionStates.Add(state);
            }
            state.PopupText = text;
            state.Kind = classification.Kind;
            state.NormalizedText = classification.NormalizedText;
            state.Hash = classification.Hash;
            state.PartIdList = classification.PartIds;
            state.ObservedAt = state.UpdatedAt = F.Clock.Now;
            await F.Db.SaveChangesAsync();
            F.Db.ChangeTracker.Clear();
            await RecomputeAsync();
        }

        public async Task GridAsync(bool active)
        {
            var grid = F.Db.GridStates.SingleOrDefault(s => s.RegionId == Region.Id);
            if (grid is null)
            {
                grid = new GridState { RegionId = Region.Id };
                F.Db.GridStates.Add(grid);
            }
            grid.Status = active ? GridStatuses.EmergencyOutages : GridStatuses.Normal;
            grid.Title = GridTitle;
            grid.Since = F.Clock.Now.AddMinutes(-5);
            grid.FeedGeneratedAt = grid.UpdatedAt = F.Clock.Now;
            await F.Db.SaveChangesAsync();
            F.Db.ChangeTracker.Clear();
            await RecomputeAsync();
        }

        public async Task RecomputeAsync()
        {
            var summary = await _coordinator.RecomputeAsync(Region.Id, CancellationToken.None);
            Assert.NotNull(summary);
            Assert.Equal(0, summary.Failed);
            F.Db.ChangeTracker.Clear();
        }

        public List<SendMessageRequest> Sent(long chat) => F.Bot.SentTo(chat).ToList();
        public List<EditMessageTextRequest> Edits(long chat) =>
            F.Bot.Edits.Where(e => e.ChatId.Identifier == chat).ToList();
        public Delivery Delivery(long chat) => Assert.Single(F.Deliveries(chat));

        public string LatestText(long chat)
        {
            var sent = Sent(chat);
            Assert.True(sent.Count > 0,
                $"Missing delivery/message: region={Region.Id}, DTEK={_part.Id}, chat={chat}, grid={F.Db.GridStates.AsNoTracking().SingleOrDefault()?.Status}");
            return Edits(chat).LastOrDefault()?.Text ?? sent.Last().Text;
        }

        public void AssertDtek(long chat)
        {
            var text = LatestText(chat);
            Assert.StartsWith($"DTEK-{chat}|", text);
            Assert.Contains(DtekTag, text);
            Assert.DoesNotContain(GridTag, text);
            Assert.Contains("MATRIX-DTEK-DETAIL.", text);
            Assert.Contains($"region=[{Region.Name}]", text);
            Assert.Contains($"parts=[{_part.Name}]", text);
            Assert.Contains(Region.SiteUrl, text);
            Assert.DoesNotContain("WRONG-LEGACY-TEMPLATE", text);
            Assert.Equal(Region.Id, Delivery(chat).RegionId);
        }

        public void AssertGrid(long chat)
        {
            var text = LatestText(chat);
            Assert.StartsWith($"GRID-{chat}|", text);
            Assert.Contains(GridTag, text);
            Assert.DoesNotContain(DtekTag, text);
            Assert.DoesNotContain("MATRIX-DTEK-DETAIL.", text);
            Assert.Contains(GridTitle, text);
            Assert.Contains("parts=[]", text);
            Assert.StartsWith("grid:emergency_outages", Delivery(chat).ContentHash);
            Assert.Equal(Region.Id, Delivery(chat).RegionId);
        }

        public void AssertClosed(long chat)
        {
            var delivery = Delivery(chat);
            Assert.True(delivery.IsClosed);
            var end = Sent(chat).Last();
            Assert.StartsWith($"ENDED-{chat}|", end.Text);
            Assert.Contains("запис про екстрені відключення", end.Text);
            Assert.Contains("Це не підтверджує", end.Text);
            Assert.Equal(delivery.TelegramMessageId, end.ReplyParameters?.MessageId);
            if (chat == Outside)
                Assert.Contains($"parts=[{_outsideName}]", end.Text);
        }

        public void AssertIsolation()
        {
            foreach (var chat in _isolated)
            {
                Assert.Empty(Sent(chat));
                Assert.Empty(Edits(chat));
                Assert.Empty(F.Deliveries(chat));
            }
        }

        public void Dispose() => F.Dispose();
    }
}
