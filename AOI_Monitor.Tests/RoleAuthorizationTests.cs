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
        Assert.False(RoleAuthorization.CanManageImageLearningTrainedData(UserRole.Operator));
        Assert.False(RoleAuthorization.CanArchiveImageLearningProjects(UserRole.Operator));
        Assert.False(RoleAuthorization.CanDeleteImageLearningArtifacts(UserRole.Operator));
    }

    [Fact]
    public void RoleAuthorizationAllowsEngineerAndAdminToRunAiTrainingSetup()
    {
        Assert.True(RoleAuthorization.CanImportImageLearningImages(UserRole.Engineer));
        Assert.True(RoleAuthorization.CanRunImageOnlyLearning(UserRole.Engineer));
        Assert.True(RoleAuthorization.CanExportImageLearningReports(UserRole.Engineer));
        Assert.True(RoleAuthorization.CanManageImageLearningTrainedData(UserRole.Engineer));
        Assert.False(RoleAuthorization.CanArchiveImageLearningProjects(UserRole.Engineer));
        Assert.False(RoleAuthorization.CanDeleteImageLearningArtifacts(UserRole.Engineer));
        Assert.True(RoleAuthorization.CanImportImageLearningImages(UserRole.Admin));
        Assert.True(RoleAuthorization.CanRunImageOnlyLearning(UserRole.Admin));
        Assert.True(RoleAuthorization.CanExportImageLearningReports(UserRole.Admin));
        Assert.True(RoleAuthorization.CanManageImageLearningTrainedData(UserRole.Admin));
        Assert.True(RoleAuthorization.CanArchiveImageLearningProjects(UserRole.Admin));
        Assert.True(RoleAuthorization.CanDeleteImageLearningArtifacts(UserRole.Admin));
    }

    [Fact]
    public void ReportsPageIsViewableByEveryRoleButExportStaysAdminOnly()
    {
        // Log & Export is viewable by every role (spec reserves only the export action for Admin).
        Assert.True(RoleAuthorization.CanAccessPage(UserRole.Operator, "reports"));
        Assert.True(RoleAuthorization.CanAccessPage(UserRole.Engineer, "reports"));
        Assert.True(RoleAuthorization.CanAccessPage(UserRole.Admin, "reports"));

        // Exporting logs remains restricted to Admin.
        Assert.False(RoleAuthorization.CanExportLogs(UserRole.Operator));
        Assert.False(RoleAuthorization.CanExportLogs(UserRole.Engineer));
        Assert.True(RoleAuthorization.CanExportLogs(UserRole.Admin));
    }

    [Fact]
    public void AiModelTestPageIsViewableByOperatorButRunningIsEngineerOrAdmin()
    {
        // Operators may open the AI Model Test page to view results...
        Assert.True(RoleAuthorization.CanAccessPage(UserRole.Operator, "modeltest"));
        // ...but running batch validation is restricted to Engineer/Admin (RTM AI-001).
        Assert.False(RoleAuthorization.CanRunModelTests(UserRole.Operator));
        Assert.True(RoleAuthorization.CanRunModelTests(UserRole.Engineer));
        Assert.True(RoleAuthorization.CanRunModelTests(UserRole.Admin));
    }
}
