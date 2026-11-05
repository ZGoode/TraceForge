using TraceForge.Domain;
using Xunit;

namespace TraceForge.Domain.Tests;

public sealed class IdentityTests
{
    [Fact]
    public void New_objects_have_distinct_immutable_internal_ids()
    {
        var a = EngineeringObject.Create(EngineeringObjectType.Requirement, "SYS-001", "A");
        var b = EngineeringObject.Create(EngineeringObjectType.Requirement, "SYS-002", "B");
        Assert.NotEqual(a.Id, b.Id);
        Assert.Equal(1, a.Revision);
    }

    [Fact]
    public void Decomposition_rejects_self_link()
    {
        var id = ObjectId.New();
        Assert.Throws<ArgumentException>(() => TypedRelationship.Create(id, id, RelationshipType.Decomposes));
    }

    [Theory]
    [InlineData("", "T")]
    [InlineData("  ", "T")]
    [InlineData("R-1", "")]
    [InlineData("R-1", " ")]
    public void Objects_require_canonical_id_and_title(string canonical, string title) =>
        Assert.Throws<ArgumentException>(() => EngineeringObject.Create(EngineeringObjectType.Requirement, canonical, title));

    [Fact]
    public void Object_can_reference_its_container()
    {
        var c = Container.Create(ContainerKind.Module, " Sys ");
        var o = EngineeringObject.Create(EngineeringObjectType.Requirement, "R-1", "T", c.Id);
        Assert.Equal(c.Id, o.ContainerId);
        Assert.Equal("Sys", c.Name);
    }

    [Fact]
    public void Container_requires_a_name() => Assert.Throws<ArgumentException>(() => Container.Create(ContainerKind.Folder, " "));

    [Fact]
    public void ObjectId_round_trips_through_its_text_form()
    {
        var id = ObjectId.New();
        Assert.Equal(id, ObjectId.Parse(id.ToString()));
    }
}
