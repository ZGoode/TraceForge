using TraceForge.Domain;
using Xunit;
namespace TraceForge.Domain.Tests;

public sealed class ReqIfTests
{
    private static ReqIfExport Sample()
    {
        string a = ObjectId.New().ToString(), b = ObjectId.New().ToString(), c = ObjectId.New().ToString(), d = ObjectId.New().ToString();
        var none = new Dictionary<string, string>();
        var srs = new ReqIfModule("SRS", [
            new ReqIfObject(a, "SRS-001", "Introduction", true, 0, none),
            new ReqIfObject(b, "SRS-002", "The **system** shall boot <2 s & log", false, 1, new Dictionary<string, string> { ["Priority"] = "high", ["Owner"] = "Ann" }),
            new ReqIfObject(c, "SRS-003", "Second", false, 1, new Dictionary<string, string> { ["Priority"] = "low" })]);
        var sdd = new ReqIfModule("SDD", [new ReqIfObject(d, "SDD-001", "Loader", false, 0, none)]);
        return new ReqIfExport("Test", ["Priority", "Owner"], [srs, sdd], [new ReqIfRelation(b, d, "Decomposes")]);
    }

    [Fact] public void Written_files_read_back_with_text_ids_headings_attributes_and_hierarchy()
    {
        var xml = ReqIf.Write(Sample(), DateTimeOffset.UtcNow);
        var read = ReqIf.Read(xml);
        Assert.Equal(new[] { "SRS", "SDD" }, read.SpecificationNames.ToArray());
        Assert.Equal(3, read.ObjectCount);
        Assert.Equal(1, read.RelationCount);
        var header = read.Table[0];
        Assert.Equal(new[] { "Number", "ID", "Heading", "Text", "Priority", "Owner" }, header.ToArray());
        Assert.Equal(new[] { "1", "SRS-001", "yes", "Introduction", "", "" }, read.Table[1].ToArray());
        Assert.Equal(new[] { "1.1", "SRS-002", "", "The **system** shall boot <2 s & log", "high", "Ann" }, read.Table[2].ToArray());
        Assert.Equal("1.2", read.Table[3][0]);
        var second = ReqIf.Read(xml, 1);
        Assert.Equal("SDD-001", second.Table[1][1]);
        Assert.Contains(read.Warnings, w => w.Contains("relation"));
    }

    [Fact] public void Reads_xhtml_enumerations_and_chapter_names_from_other_tools()
    {
        const string xml = """
<?xml version="1.0" encoding="UTF-8"?>
<REQ-IF xmlns="http://www.omg.org/spec/ReqIF/20110401/reqif.xsd" xmlns:xhtml="http://www.w3.org/1999/xhtml">
 <THE-HEADER><REQ-IF-HEADER IDENTIFIER="h"><TITLE>Foreign</TITLE></REQ-IF-HEADER></THE-HEADER>
 <CORE-CONTENT><REQ-IF-CONTENT>
  <DATATYPES><DATATYPE-DEFINITION-ENUMERATION IDENTIFIER="dt-e" LONG-NAME="Prio"><SPECIFIED-VALUES><ENUM-VALUE IDENTIFIER="ev-hi" LONG-NAME="High"/></SPECIFIED-VALUES></DATATYPE-DEFINITION-ENUMERATION></DATATYPES>
  <SPEC-TYPES><SPEC-OBJECT-TYPE IDENTIFIER="t"><SPEC-ATTRIBUTES>
    <ATTRIBUTE-DEFINITION-XHTML IDENTIFIER="a-text" LONG-NAME="ReqIF.Text"/>
    <ATTRIBUTE-DEFINITION-XHTML IDENTIFIER="a-chap" LONG-NAME="ReqIF.ChapterName"/>
    <ATTRIBUTE-DEFINITION-ENUMERATION IDENTIFIER="a-prio" LONG-NAME="Priority"/>
  </SPEC-ATTRIBUTES></SPEC-OBJECT-TYPE></SPEC-TYPES>
  <SPEC-OBJECTS>
   <SPEC-OBJECT IDENTIFIER="o1"><VALUES><ATTRIBUTE-VALUE-XHTML><DEFINITION><ATTRIBUTE-DEFINITION-XHTML-REF>a-chap</ATTRIBUTE-DEFINITION-XHTML-REF></DEFINITION><THE-VALUE><xhtml:div>Scope</xhtml:div></THE-VALUE></ATTRIBUTE-VALUE-XHTML></VALUES></SPEC-OBJECT>
   <SPEC-OBJECT IDENTIFIER="o2"><VALUES>
     <ATTRIBUTE-VALUE-XHTML><DEFINITION><ATTRIBUTE-DEFINITION-XHTML-REF>a-text</ATTRIBUTE-DEFINITION-XHTML-REF></DEFINITION><THE-VALUE><xhtml:div>The  tool
       shall <xhtml:b>work</xhtml:b>.</xhtml:div></THE-VALUE></ATTRIBUTE-VALUE-XHTML>
     <ATTRIBUTE-VALUE-ENUMERATION><DEFINITION><ATTRIBUTE-DEFINITION-ENUMERATION-REF>a-prio</ATTRIBUTE-DEFINITION-ENUMERATION-REF></DEFINITION><VALUES><ENUM-VALUE-REF>ev-hi</ENUM-VALUE-REF></VALUES></ATTRIBUTE-VALUE-ENUMERATION>
   </VALUES></SPEC-OBJECT>
  </SPEC-OBJECTS>
  <SPECIFICATIONS><SPECIFICATION IDENTIFIER="s" LONG-NAME="Doc"><CHILDREN>
    <SPEC-HIERARCHY IDENTIFIER="h1"><OBJECT><SPEC-OBJECT-REF>o1</SPEC-OBJECT-REF></OBJECT><CHILDREN>
      <SPEC-HIERARCHY IDENTIFIER="h2"><OBJECT><SPEC-OBJECT-REF>o2</SPEC-OBJECT-REF></OBJECT></SPEC-HIERARCHY>
      <SPEC-HIERARCHY IDENTIFIER="h3"><OBJECT><SPEC-OBJECT-REF>missing</SPEC-OBJECT-REF></OBJECT></SPEC-HIERARCHY>
    </CHILDREN></SPEC-HIERARCHY>
  </CHILDREN></SPECIFICATION></SPECIFICATIONS>
 </REQ-IF-CONTENT></CORE-CONTENT>
</REQ-IF>
""";
        var read = ReqIf.Read(xml);
        Assert.Equal(2, read.ObjectCount);
        Assert.Equal(new[] { "1", "", "yes", "Scope", "" }, read.Table[1].ToArray());
        Assert.Equal(new[] { "1.1", "", "", "The tool shall work.", "High" }, read.Table[2].ToArray());
        Assert.Contains(read.Warnings, w => w.Contains("not in the file"));
    }

    [Fact] public void Refuses_dtds_and_non_reqif_documents()
    {
        Assert.Throws<FormatException>(() => ReqIf.Read("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><REQ-IF>&e;</REQ-IF>"));
        Assert.Throws<FormatException>(() => ReqIf.Read("<html/>"));
        Assert.Throws<FormatException>(() => ReqIf.Read("not xml at all"));
        Assert.Throws<FormatException>(() => ReqIf.Read("<REQ-IF xmlns=\"http://www.omg.org/spec/ReqIF/20110401/reqif.xsd\"><CORE-CONTENT><REQ-IF-CONTENT/></CORE-CONTENT></REQ-IF>"));
    }
}
