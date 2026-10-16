using DtekBot.Api.Data;
using DtekBot.Shared;

namespace DtekBot.Tests;

internal static class ChannelAssert
{
    // A channel the user sees as brand new: unsubscribed, every per-chat setting at its default.
    public static void IsCleanUnsubscribed(Channel channel)
    {
        Assert.False(channel.IsActive);
        Assert.Equal("", channel.RegionId);
        Assert.Null(channel.PartId);
        Assert.Null(channel.PartIds);
        Assert.False(channel.IsPaused);
        Assert.Equal(AlertFilter.All, channel.Filter);
        Assert.True(channel.NotifyOnEnd);
        Assert.True(channel.EditInPlace);
        Assert.Null(channel.Template);
        Assert.Null(channel.Templates);
        Assert.False(channel.OutageTrigger);
        Assert.Null(channel.OutageTriggerPattern);
        Assert.Null(channel.RecentPosts);
        Assert.False(channel.ScheduleNotify);
        Assert.Equal(ScheduleImage.None, channel.ScheduleImage);
        Assert.Equal(PostMode.Edit, channel.ScheduleEventMode);
        Assert.False(channel.SchedulePost);
        Assert.Equal(PostMode.New, channel.SchedulePostMode);
        Assert.Equal(ScheduleImage.Grid, channel.SchedulePostImage);
        Assert.Null(channel.GroupId);
        Assert.Null(channel.AddressLabel);
        Assert.Null(channel.AddressStreet);
        Assert.Null(channel.AddressHouse);
    }

    // Fills every per-chat setting with a non-default value.
    public static void Customize(Channel c)
    {
        c.RegionId = "kyiv";
        c.PartId = "old-part";
        c.PartIds = "right-bank,left-bank";
        c.IsActive = true;
        c.IsPaused = true;
        c.Filter = AlertFilter.EmergencyOnly;
        c.NotifyOnEnd = false;
        c.EditInPlace = false;
        c.Template = "<b>{message}</b>";
        c.Templates = "{\"emergency\":\"x\"}";
        c.OutageTrigger = true;
        c.OutageTriggerPattern = "світла нема";
        c.RecentPosts = "[\"a\"]";
        c.ScheduleNotify = true;
        c.ScheduleImage = ScheduleImage.Grid;
        c.ScheduleEventMode = PostMode.ReplaceDelete;
        c.SchedulePost = true;
        c.SchedulePostMode = PostMode.Edit;
        c.SchedulePostImage = ScheduleImage.None;
        c.GroupId = "45.1";
        c.AddressLabel = "вул. А, 1";
        c.AddressStreet = "вул. А";
        c.AddressHouse = "1";
    }
}
