<%@ Page Title="" Language="C#" MasterPageFile="~/Views/Shared/Site.Master" Inherits="System.Web.Mvc.ViewPage<IEnumerable<Cydin.Models.VcsSource>>" %>

<asp:Content ID="Content1" ContentPlaceHolderID="TitleContent" runat="server">
	Index
</asp:Content>

<asp:Content ID="Content2" ContentPlaceHolderID="MainContent" runat="server">

    <h2>Version Control Sources</h2>

    <table class="listview">
        <tr>
            <th>
                Url
            </th>
            <th>
                Repository Type
            </th>
            <th>
                Auto Publish
            </th>
            <th></th>
        </tr>

    <% foreach (var item in Model.Where (s => s.Type != "Upload")) { %>
    
        <tr>
            <td>
                <%= Html.Encode(item.Url) %>
            </td>
            <td>
                <%= Html.Encode(item.Type) %>
            </td>
            <td>
                <%= Html.Encode(item.AutoPublish) %>
            </td>
            <td>
                <%= Html.ActionLink("Edit", "Edit", new { id=item.Id }, new { @class="button" }) %> 
                <%= Html.ActionLink ("Delete", "Delete", new { id = item.Id, projectId = ViewData["ProjectId"] }, new { @class="button" })%>
            </td>
        </tr>
    
    <% } %>

    </table>

    <p>
        <%= Html.ActionLink ("Create New", "Create", new { projectId = ViewData["ProjectId"] }, new { @class="button" })%>
    </p>
    <p>
        <%= Html.ActionLink ("Back to Project", "Index", "Project", new { id = ViewData["ProjectId"] }, null)%>
    </p>

</asp:Content>

