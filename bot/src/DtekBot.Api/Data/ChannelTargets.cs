using System.Net;
using DtekBot.Shared;

namespace DtekBot.Api.Data;

public sealed record ChannelGroupTarget(string RegionId, string GroupId, long? AddressId = null);

public static class ChannelTargets
{
    public static IReadOnlyList<string> RegionIds(this Channel channel) => channel.Addresses.Count == 0
        ? (string.IsNullOrEmpty(channel.RegionId) ? [] : [channel.RegionId])
        : channel.Addresses.Select(a => a.RegionId).Distinct(StringComparer.Ordinal).ToArray();

    public static Channel ForRegion(this Channel channel, string regionId)
    {
        var copy = channel.Copy();
        copy.IsMultiRegion = channel.IsMultiRegion || channel.RegionIds().Count > 1;
        copy.Addresses = channel.Addresses.Where(a => a.RegionId == regionId).ToList();
        if (channel.RegionId != regionId)
        {
            copy.PartId = null;
            copy.PartIds = null;
            copy.GroupId = null;
            copy.AddressCity = null;
            copy.AddressStreet = null;
            copy.AddressHouse = null;
            copy.AddressLabel = null;
        }
        copy.RegionId = regionId;
        if (channel.Addresses.Count > 0) copy.MirrorPrimary();
        return copy;
    }

    public static IReadOnlyList<ChannelGroupTarget> GroupTargets(this Channel channel) =>
        channel.RegionIds().SelectMany(id => channel.ForRegion(id).GroupIds()
            .Select(group => new ChannelGroupTarget(id, group))).ToArray();

    public static IReadOnlyList<string> GroupIds(this Channel channel) => channel.Addresses.Count == 0
        ? (string.IsNullOrEmpty(channel.GroupId) ? [] : [channel.GroupId])
        : channel.Addresses.SelectMany(a => a.Groups).Select(g => g.GroupId)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    // The chosen main group while the chat still has it; otherwise the first group of the first address.
    public static string? SvitlobotGroup(this Channel channel)
    {
        return channel.SvitlobotTarget()?.GroupId;
    }

    public static ChannelGroupTarget? SvitlobotTarget(this Channel channel)
    {
        if (channel.Addresses.Count == 0)
            return string.IsNullOrEmpty(channel.GroupId) ? null : new(channel.RegionId, channel.GroupId);
        var targets = channel.GroupTargets();
        if (channel.SvitlobotGroupId is { } chosen)
        {
            if (channel.SvitlobotAddressId is { } addressId)
            {
                var address = channel.Addresses.FirstOrDefault(a => a.Id == addressId
                    && a.RegionId == channel.SvitlobotRegionId && a.Groups.Any(g => g.GroupId == chosen));
                if (address is not null) return new(address.RegionId, chosen, address.Id);
            }
            var match = targets.FirstOrDefault(t => t.GroupId == chosen
                && t.RegionId == (channel.SvitlobotRegionId ?? channel.RegionId));
            if (match is not null) return match;
        }
        return channel.Addresses.OrderBy(a => a.Id == 0 ? long.MaxValue : a.Id)
                .SelectMany(a => a.Groups.OrderBy(g => g.GroupId, StringComparer.Ordinal)
                    .Select(g => new ChannelGroupTarget(a.RegionId, g.GroupId, a.Id))).FirstOrDefault();
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

    public static void AddTemplateContext(this Channel channel, IDictionary<string, string> values,
        IEnumerable<ChatAddress>? matchingAddresses = null, IReadOnlyList<string>? groupIds = null)
    {
        var addresses = (matchingAddresses ?? channel.Addresses).ToArray();
        values["regionId"] = channel.RegionId;
        values["city"] = string.Join(", ", (channel.Addresses.Count == 0 ? new[] { channel.AddressCity } : addresses.Select(a => a.City))
            .Where(c => !string.IsNullOrWhiteSpace(c)).Select(c => c!.Trim()).Distinct(StringComparer.OrdinalIgnoreCase));
        values["address"] = channel.AddressText(addresses);
        values["group"] = string.Join(", ", groupIds ?? (channel.Addresses.Count == 0 ? channel.GroupIds()
            : addresses.SelectMany(a => a.Groups).Select(g => g.GroupId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()));
    }

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
        var mainAddresses = channel.Addresses.Where(a => a.RegionId == channel.RegionId).ToArray();
        var first = mainAddresses.OrderBy(a => a.Id == 0 ? long.MaxValue : a.Id).FirstOrDefault();
        channel.GroupId = mainAddresses.SelectMany(a => a.Groups).Select(g => g.GroupId).Distinct().Order(StringComparer.Ordinal).FirstOrDefault();
        channel.AddressCity = first?.City;
        channel.AddressStreet = first?.Street;
        channel.AddressHouse = first?.House;
        channel.AddressLabel = first?.Label ?? first?.Name;
    }

    public static void UpdateLegacyPrimary(this Channel channel)
    {
        var first = channel.Addresses.Where(a => a.RegionId == channel.RegionId).OrderBy(a => a.Id).FirstOrDefault();
        if (first is null)
        {
            if (channel.GroupId is null && channel.AddressStreet is null && channel.AddressLabel is null) return;
            first = new ChatAddress { ChatId = channel.ChatId, RegionId = channel.RegionId, Name = "Дім" };
            channel.Addresses.Add(first);
        }
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
