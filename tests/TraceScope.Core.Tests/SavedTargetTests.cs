using System.Net;
using System.Text;
using TraceScope.Core;
using Xunit;

namespace TraceScope.Core.Tests;

public sealed class SavedTargetTests
{
    [Fact]
    public async Task LibraryRoundTripPreservesInactiveStoppedDuplicateDefinitionsAndAllSettings()
    {
        var options = new ProbeOptions { Protocol=ProbeProtocol.Tcp,DestinationPort=8443,MaxHops=5 };
        var configuration = new WorkspaceConfiguration { Groups=["Work","Empty"], RestoreOnStartup=false,
            Targets=[new("127.0.0.1", options,"Work",Id:Guid.NewGuid(),Closed:true,DisplayName:"Router"),
                new("127.0.0.1", options,"Work",Id:Guid.NewGuid(),Stopped:true,SessionId:Guid.NewGuid(),ResolvedAddress:"127.0.0.1")],
            Profiles=[new("Service",options)], Alerts=[new() { Target="127.0.0.1",Group="Work" }] };
        using var stream = new MemoryStream(); await WorkspaceStore.ExportAsync(stream,configuration); stream.Position=0;
        var restored = await WorkspaceStore.ImportAsync(stream);
        Assert.Equal(configuration.Targets,restored.Targets); Assert.Equal(configuration.Groups,restored.Groups);
        Assert.Equal(configuration.Profiles,restored.Profiles); Assert.Equal(configuration.Alerts,restored.Alerts);
        Assert.False(restored.RestoreOnStartup); Assert.Equal(1,restored.Version);
    }

    [Fact]
    public async Task LegacyWorkspaceStillLoadsWithOpenRunningAndPausedSemantics()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("""
            {"Version":1,"Targets":[{"Target":"localhost","Options":{},"Group":"Work","Paused":true}]}
            """));
        var restored = await WorkspaceStore.ImportAsync(stream);
        var target = Assert.Single(restored.Targets);
        Assert.Equal(Guid.Empty,target.Id); Assert.True(target.Paused); Assert.False(target.Stopped); Assert.False(target.Closed);
        Assert.Contains("Work",TargetGroupCatalog.List(restored));
    }

    [Fact]
    public void SavedIdsMustBeUniqueButAddressesNeedNotBe()
    {
        var id=Guid.NewGuid(); var target=new WorkspaceTarget("localhost",new(),Id:id,Closed:true);
        Assert.Throws<InvalidDataException>(()=>new WorkspaceConfiguration { Targets=[target,target] }.Validate());
        new WorkspaceConfiguration { Targets=[target,target with { Id=Guid.NewGuid() }] }.Validate();
    }

    [Fact]
    public async Task RestoringStoppedTabPreservesSessionIdWithoutDnsOrProbeWork()
    {
        var directory=Path.Combine(Path.GetTempPath(),"pngr-stopped-test",Guid.NewGuid().ToString("N"));
        var transport=new Transport();
        try
        {
            await using var engine=new TraceEngine(Path.Combine(directory,"history.db"),transport:transport);
            var id=Guid.NewGuid();
            var session=await engine.OpenStoppedTargetAsync(new("unresolvable.invalid",new(),Id:Guid.NewGuid(),Stopped:true,SessionId:id));
            Assert.Equal(id,session.Id); Assert.Equal(SessionState.Stopped,session.State); Assert.Equal(0,transport.Calls);
            engine.Resume(id); Assert.Equal(SessionState.Stopped,session.State);
            await engine.StopAsync(id); Assert.True(engine.ForgetSession(id)); Assert.Empty(engine.Sessions);
        }
        finally { Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools(); if(Directory.Exists(directory)) Directory.Delete(directory,true); }
    }
    private sealed class Transport:IProbeTransport
    {
        public int Calls;
        public Task<ProbeResult> ProbeAsync(IPAddress address,int ttl,TimeSpan timeout,CancellationToken cancellationToken)
        { Calls++; throw new InvalidOperationException("Stopped targets must never probe."); }
    }
}
