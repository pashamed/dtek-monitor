using System.Net;
using DtekBot.Shared;

namespace DtekBot.Api.Data;

public static class ChannelTargets
{
    public static IReadOnlyList<string> GroupIds(this Channel channel) => channel.Addresses.Count == 0
        ? (string.IsNullOrEmpty(channel.GroupId) ? [] : [channel.GroupId])
        : channel.Addresses.SelectMany(a => a.Groups).Select(g => g.GroupId)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    // The chosen main group while the chat still has it; otherwise the first group of the first address.
    public static string? SvitlobotGroup(this Channel channel)
    {
        var groups = channel.GroupIds();
        if (channel.SvitlobotGroupId is { } chosen && groups.Contains(chosen, StringComparer.Ordinal))
        {
            return chosen;
        }

        return channel.Addresses.Count == 0
            ? groups.FirstOrDefault()
            : channel.Addresses.OrderBy(a => a.Id == 0 ? long.MaxValue : a.Id)
                .SelectMany(a => a.Groups.Select(g => g.GroupId).Order(StringComparer.Ordinal))
                .FirstOrDefault();
    }

    public static bool IsCombined(this Channel channel) => channel.Addresses.Count > 1 || channel.GroupIds().Count > 1;

    public static IReadOnlyList<string> DistrictIds(this Channel channel) =>
        channel.PartIdList.Concat(channel.Addresses.SelectMany(a =>
            a.PartIds?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) ?? []))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    public static IReadOnlyList<string> PopupDistrictIds(this Channel channel)
    {
        if (channel.Addresses.Count == 0) return channel.PartIdList;
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var address in channel.Addresses)
        {
            IReadOnlyList<string> parts = address.PartIds?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                ?? channel.PartIdList;
            // An unrestricted address makes the subscription region-wide.
            if (parts.Count == 0) return [];
            result.UnionWith(parts);
        }
        return result.ToArray();
    }

    public static string AddressText(this Channel channel, IEnumerable<ChatAddress>? addresses = null) =>
        channel.Addresses.Count == 0 ? channel.AddressLabel ?? ""
        : string.Join("; ", (addresses ?? channel.Addresses).Select(a =>
            !string.IsNullOrWhiteSpace(a.Label) ? a.Label
            : string.Join(", ", new[] { a.City, a.Street, a.House }.Where(v => !string.IsNullOrWhiteSpace(v))))
            .Where(v => !string.IsNullOrWhiteSpace(v)).Distinct());

    public static string GroupNames(this Channel channel, string groupId) => string.Join(", ",
        channel.Addresses.Where(a => a.Groups.Any(g => g.GroupId == groupId)).Select(a =>
        {
            var label = a.Groups.First(g => g.GroupId == groupId).Label;
            var name = string.IsNullOrWhiteSpace(a.Name) ? a.Label ?? a.Street ?? "Адреса" : a.Name;
            return string.IsNullOrWhiteSpace(label) ? name : $"{name} · {label}";
        }).Distinct());

    public static string Heading(this Channel channel, string groupId) =>
        $"<b>Черга {WebUtility.HtmlEncode(groupId)}</b>" +
        (string.IsNullOrEmpty(channel.GroupNames(groupId)) ? "" : $" · {WebUtility.HtmlEncode(channel.GroupNames(groupId))}");

    // Scalar columns remain a readable projection for older clients and existing integrations.
    public static void MirrorPrimary(this Channel channel)
    {
        var first = channel.Addresses.OrderBy(a => a.Id == 0 ? long.MaxValue : a.Id).FirstOrDefault();
        channel.GroupId = channel.Addresses.SelectMany(a => a.Groups).Select(g => g.GroupId).Distinct().Order(StringComparer.Ordinal).FirstOrDefault();
        channel.AddressCity = first?.City;
        channel.AddressStreet = first?.Street;
        channel.AddressHouse = first?.House;
        channel.AddressLabel = first?.Label ?? first?.Name;
    }

    public static void UpdateLegacyPrimary(this Channel channel)
    {
        if (channel.Addresses.Count == 0)
        {
            if (channel.GroupId is null && channel.AddressStreet is null && channel.AddressLabel is null) return;
            channel.Addresses.Add(new ChatAddress { ChatId = channel.ChatId, RegionId = channel.RegionId, Name = "Дім" });
        }
        var first = channel.Addresses.OrderBy(a => a.Id).First();
        first.RegionId = channel.RegionId;
        first.City = channel.AddressCity; first.Street = channel.AddressStreet;
        first.House = channel.AddressHouse; first.Label = channel.AddressLabel;
        if (channel.Addresses.Count == 1 && first.Groups.Count <= 1)
        {
            if (channel.GroupId is null) first.Groups.Clear();
            else if (first.Groups.FirstOrDefault()?.GroupId != channel.GroupId)
            {
                first.Groups.Clear(); first.Groups.Add(new ChatAddressGroup { GroupId = channel.GroupId });
            }
        }
    }

    public static ChatAddressDto ToDto(this ChatAddress address) => new(address.Id, address.RegionId,
        address.Name, address.City, address.Street, address.House,
        address.Groups.Select(g => new ChatAddressGroupDto(g.GroupId, g.Label)).ToArray(), address.PartIds, address.Label);
}
