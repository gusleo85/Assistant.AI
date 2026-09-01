using System.Collections.Generic;

namespace JustLogin.Identity.SDK.DTO.Models;
public class JustLoginToken : StandardToken
{
    public string CompanyGUID { get; set; } = "";
    public string CompanyID { get; set; } = "";
    public string CompanyName { get; set; } = "";        
    public string UserGUID { get; set; } = "";
    public string Username { get; set; } = "";
    public string FullName { get; set; } = "";
    public string WorkEmail { get; set; } = "";
    public List<string> Roles { get; set; } = new List<string>();
}