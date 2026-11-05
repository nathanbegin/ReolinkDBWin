using System.Text.Json.Nodes;
using ReolinkDBWin.Core.Events;
using ReolinkDBWin.Core.Frigate;
using ReolinkDBWin.Core.Models;
using ReolinkDBWin.Core.Reolink;

namespace ReolinkDBWin.Core.Tests;

public class MonitorTests
{
    private static ReolinkEventMonitor NewReolinkMonitor() =>
        new(new ReolinkClient(new ReolinkConnectionInfo { Host = "x" }, new FakeHandler((_, _) => FakeHandler.Json("[]"))), 0, TimeSpan.FromSeconds(1));

    [Fact]
    public void Reolink_rising_edge_only()
    {
        var m = NewReolinkMonitor();
        var now = DateTimeOffset.Now;

        Assert.Empty(m.ProcessStates(new Dictionary<string, bool> { ["visitor"] = true }, now, suppress: true));
        Assert.Empty(m.ProcessStates(new Dictionary<string, bool> { ["visitor"] = true }, now));
        Assert.Empty(m.ProcessStates(new Dictionary<string, bool> { ["visitor"] = false }, now));
        var events = m.ProcessStates(new Dictionary<string, bool> { ["visitor"] = true }, now);

        Assert.Equal(DetectionType.Doorbell, Assert.Single(events).Type);
    }

    [Fact]
    public void Reolink_motion_is_dropped_when_ai_detection_fires()
    {
        var m = NewReolinkMonitor();
        var events = m.ProcessStates(new Dictionary<string, bool> { ["motion"] = true, ["person"] = true }, DateTimeOffset.Now);
        Assert.Equal(DetectionType.Person, Assert.Single(events).Type);
    }

    [Fact]
    public void Frigate_monitor_suppresses_first_batch_and_dedups()
    {
        var client = new FrigateClient(new FrigateConnectionInfo { BaseUrl = "http://f:5000" }, new FakeHandler((_, _) => FakeHandler.Json("[]")));
        var m = new FrigateEventMonitor(client, "porte", [], TimeSpan.FromSeconds(5));
        FrigateEvent Ev(string id, string label) => new() { Id = id, Camera = "porte", Label = label, StartTime = DateTimeOffset.Now };

        Assert.Empty(m.Process([Ev("a", "person")], suppress: true));
        var events = m.Process([Ev("a", "person"), Ev("b", "car")]);

        var e = Assert.Single(events);
        Assert.Equal("b", e.FrigateEventId);
        Assert.Equal(DetectionType.Vehicle, e.Type);
        Assert.Empty(m.Process([Ev("b", "car")]));
    }

    [Fact]
    public void HomeAssistant_state_changed_to_on()
    {
        var entities = new HashSet<string>(["binary_sensor.porte_visitor"], StringComparer.OrdinalIgnoreCase);
        var msg = JsonNode.Parse("""
            {"id":1,"type":"event","event":{"event_type":"state_changed","data":{"entity_id":"binary_sensor.porte_visitor",
              "old_state":{"state":"off"},"new_state":{"state":"on","attributes":{"friendly_name":"Porte Visiteur"}}}}}
            """)!;

        var e = HomeAssistantEventMonitor.ParseStateChanged(msg, entities, DateTimeOffset.Now);

        Assert.NotNull(e);
        Assert.Equal(DetectionType.Doorbell, e.Type);
        Assert.Equal("Porte Visiteur", e.RawLabel);
    }

    [Fact]
    public void HomeAssistant_ignores_other_entities_and_off()
    {
        var entities = new HashSet<string>(["binary_sensor.porte_person"]);
        var off = JsonNode.Parse("""{"type":"event","event":{"data":{"entity_id":"binary_sensor.porte_person","old_state":{"state":"on"},"new_state":{"state":"off"}}}}""")!;
        var other = JsonNode.Parse("""{"type":"event","event":{"data":{"entity_id":"light.salon","old_state":{"state":"off"},"new_state":{"state":"on"}}}}""")!;
        Assert.Null(HomeAssistantEventMonitor.ParseStateChanged(off, entities, DateTimeOffset.Now));
        Assert.Null(HomeAssistantEventMonitor.ParseStateChanged(other, entities, DateTimeOffset.Now));
    }

    [Theory]
    [InlineData("binary_sensor.sonnette_visitor", DetectionType.Doorbell)]
    [InlineData("binary_sensor.sonnette_person", DetectionType.Person)]
    [InlineData("binary_sensor.sonnette_vehicle", DetectionType.Vehicle)]
    [InlineData("binary_sensor.sonnette_pet", DetectionType.Animal)]
    [InlineData("binary_sensor.sonnette_package", DetectionType.Package)]
    [InlineData("binary_sensor.sonnette_motion", DetectionType.Motion)]
    [InlineData("binary_sensor.porte_person_occupancy", DetectionType.Person)]
    [InlineData("binary_sensor.inconnu", DetectionType.Other)]
    [InlineData("binary_sensor.sonnette_visiteur", DetectionType.Doorbell)]
    [InlineData("binary_sensor.sonnette_personne", DetectionType.Person)]
    [InlineData("binary_sensor.front_doorbell_person", DetectionType.Person)]
    public void Entity_id_maps_to_type(string id, DetectionType expected) =>
        Assert.Equal(expected, DetectionTypeInfo.FromEntityId(id));
}
