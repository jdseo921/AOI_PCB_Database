using AOI_Monitor.Services;
using Xunit;

namespace AOI_Monitor.Tests;

public sealed class RoleAuthorizationTests
{
    [Fact]
    public void RoleAuthorizationAllowsOperatorToViewAiTrainingSetupButNotRunIt()
    {
        Assert.True(RoleAuthorization.CanAccessPage(UserRole.Operator, "modeltest"));
        Assert.False(RoleAuthorization.CanImportImageLearningImages(UserRole.Operator));
        Assert.False(RoleAuthorization.CanRunImageOnlyLearning(UserRole.Operator));
        Assert.False(RoleAuthorization.CanExportImageLearningReports(UserRole.Operator));
    }

    [Fact]
    public void RoleAuthorizationAllowsEngineerAndAdminToRunAiTrainingSetup()
    {
        Assert.True(RoleAuthorization.CanImportImageLearningImages(UserRole.Engineer));
        Assert.True(RoleAuthorization.CanRunImageOnlyLearning(UserRole.Engineer));
        Assert.True(RoleAuthorization.CanExportImageLearningReports(UserRole.Engineer));
        Assert.True(RoleAuthorization.CanImportImageLearningImages(UserRole.Admin));
        Assert.True(RoleAuthorization.CanRunImageOnlyLearning(UserRole.Admin));
        Assert.True(RoleAuthorization.CanExportImageLearningReports(UserRole.Admin));
    }
}
