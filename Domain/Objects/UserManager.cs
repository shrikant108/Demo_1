using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.DirectoryServices.AccountManagement;
using Dapper;
using System.Data.SqlClient;
using System.Configuration;

namespace Domain
{
    public static class UserManager
    {
        private static readonly string ConnectionString = ConfigurationManager.AppSettings.Get("Connection");

        public static User Login(string userName, string password)
        {
            using (var context = new PrincipalContext(ContextType.Domain, "erccollections.com"))
            {
                var usr = UserPrincipal.FindByIdentity(context, userName);
                if (usr != null)
                {
                    var verified = context.ValidateCredentials(userName, password);
                    if (verified)
                    {
                        using (var conn = new SqlConnection(ConnectionString))
                        {
                            conn.Open();
                            var user =
                                conn.Query<User>(
                                    "select * from [collect2000].[ERCTasks].[Users] WHERE UserName = @userName",
                                    new { userName = userName }).FirstOrDefault();
                            if (user == null)
                            {
                                var userId = conn.ExecuteScalar<int>(
                                    "INSERT INTO [collect2000].[ERCTasks].[Users] (UserName, DisplayName, Email) VALUES (@userName, @dispName, @email); SELECT SCOPE_IDENTITY();",
                                    new { userName = userName, dispName = usr.DisplayName, email = usr.EmailAddress });
                                user = new User()
                                {
                                    DisplayName = usr.DisplayName,
                                    UserName = userName,
                                    UserRole = "user",
                                    UserId = userId,
                                    Email = usr.EmailAddress
                                };
                            }
                            return user;
                        }
                    }
                }
            }
            return null;
        }

        public static User GetUserDetails(string username)
        {
            User _objUser = new User();
            using (var context = new PrincipalContext(ContextType.Domain, "erccollections.com"))
            {
                var usr = UserPrincipal.FindByIdentity(context, username);
                if (usr != null)
                {
                    
                    _objUser.DisplayName = usr.DisplayName;
                    _objUser.UserName = username;
                    _objUser.Email = usr.EmailAddress;
                    _objUser.UserRole = "user";
                }
            }
            return _objUser;
        }
    }
}