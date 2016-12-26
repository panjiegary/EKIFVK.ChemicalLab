﻿using System;
using System.Linq;
using System.Collections;
using System.Collections.Generic;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Newtonsoft.Json.Linq;
using EKIFVK.ChemicalLab.Models;
using EKIFVK.ChemicalLab.SearchFilter;
using EKIFVK.ChemicalLab.Configurations;
using EKIFVK.ChemicalLab.Services.Authentication;

//! Username is not case sensitive

namespace EKIFVK.ChemicalLab.Controllers
{
    /// <summary>
    /// API for User Management
    /// <list type="bullet">
    /// <item><description>GET /{name} => GetInfo</description></item>
    /// <item><description>POST /{name} => Register</description></item>
    /// <item><description>PUT /{name}/token => SignIn</description></item>
    /// <item><description>DELETE /{name}/token => SignOut</description></item>
    /// <item><description>DELETE /{name} => Delete</description></item>
    /// <item><description>PATCH /{name} => ChangeUserInformation</description></item>
    /// <item><description>GET /.count => GetUserCount</description></item>
    /// <item><description>GET /.list => GetUserList</description></item>
    /// </list>
    /// </summary>
    [Route("user")]
    public class UserController : BasicVerifiableController
    {
        private readonly IOptions<UserModuleConfiguration> _configuration;

        public UserController(ChemicalLabContext database, IAuthentication verifier, IOptions<UserModuleConfiguration> configuration)
            : base(database, verifier)
        {
            _configuration = configuration;
        }

        /// <summary>
        /// Get user information<br />
        /// <br />
        /// Permission Group
        /// <list type="bullet">
        /// <item><description>UserManagePermission</description></item>
        /// </list>
        /// Returned Value
        /// <list type="bullet">
        /// <item><description>{name, group, accessTime, accessAddress, allowMulti, disabled:bool, update}</description></item>
        /// </list>
        /// Probable Errors
        /// <list type="bullet">
        /// <item><description>No target user: 404 NoTargetUser</description></item>
        /// <item><description>Permission denied: 403 [VerifyResult]</description></item>
        /// </list>
        /// </summary>
        /// <param name="name">Target user's name</param>
        [HttpGet("{name}")]
        public JsonResult GetInfo(string name)
        {
            var user = FindUser();
            if (!Verify(user, _configuration.Value.UserManagePermission, out var verifyResult)) return Basic403(verifyResult);
            user = FindUser(name);
            if (user == null) return BasicResponse(StatusCodes.Status404NotFound, _configuration.Value.NoTargetUser);
            return BasicResponse(data: new Hashtable
            {
                {"name", user.Name},
                {"group", user.UserGroupNavigation.Name},
                {"accessTime", user.LastAccessTime},
                {"accessAddress", user.LastAccessAddress},
                {"allowMulti", user.AllowMultiAddressLogin},
                {"disabled", user.Disabled},
                {"update", user.LastUpdate}
            });
        }

        /// <summary>
        /// Register<br />
        /// <br />
        /// Permission Group
        /// <list type="bullet">
        /// <item><description>UserAddingPermission</description></item>
        /// </list>
        /// Returned Value
        /// <list type="bullet">
        /// <item><description>id:int</description></item>
        /// </list>
        /// Probable Errors
        /// <list type="bullet">
        /// <item><description>Permission denied: 403 [VerifyResult]</description></item>
        /// <item><description>Invalid username format: 400 InvalidUsernameFormat</description></item>
        /// <item><description>Invalid password format: 400 InvalidPasswordFormat</description></item>
        /// <item><description>User already exist: 409 UserAlreadyExist</description></item>
        /// </list>
        /// </summary>
        /// <param name="name">User's name (cannot have /\?, first letter cannot be .)</param>
        /// <param name="parameter">
        /// Parameters<br />
        /// <list type="bullet">
        /// <item><description>password: Uppercase SHA256 of password</description></item>
        /// </list>
        /// </param>
        [HttpPost("{name}")]
        public JsonResult Register(string name, [FromBody] Hashtable parameter)
        {
            var user = FindUser();
            if (!Verify(user, _configuration.Value.UserAddingPermission, out var verifyResult)) return Basic403(verifyResult);
            if (string.IsNullOrEmpty(name) ||
                name.IndexOf("/", StringComparison.Ordinal) > -1 ||
                name.IndexOf("\\", StringComparison.Ordinal) > -1 ||
                name.IndexOf("?", StringComparison.Ordinal) > -1 ||
                name.IndexOf(".", StringComparison.Ordinal) == 0)
                return BasicResponse(StatusCodes.Status400BadRequest, _configuration.Value.InvalidUsernameFormat);
            var password = parameter["password"].ToString();
            if (password.ToUpper() != password || password.Length != 64)
                return BasicResponse(StatusCodes.Status400BadRequest, _configuration.Value.InvalidPasswordFormat);
            user = FindUser(name);
            if (user != null) return BasicResponse(StatusCodes.Status409Conflict, _configuration.Value.UserAlreadyExist);
            var normalUsergroupId = _configuration.Value.DefaultUserGroup;
            user = new User
            {
                Name = name,
                Password = password,
                UserGroupNavigation = Database.UserGroups.FirstOrDefault(e => e.Id == normalUsergroupId),
                LastUpdate = DateTime.Now
            };
            Database.Users.Add(user);
            Database.SaveChanges();
            return BasicResponse(data: user.Id);
        }

        /// <summary>
        /// User sign in<br />
        /// <br />
        /// Permission Group
        /// <list type="bullet">
        /// <item><description>NULL</description></item>
        /// </list>
        /// Returned Value
        /// <list type="bullet">
        /// <item><description>token</description></item>
        /// </list>
        /// Probable Errors
        /// <list type="bullet">
        /// <item><description>No target user: 404 NoTargetUser</description></item>
        /// <item><description>User is disabled: 403 DisabledUser</description></item>
        /// </list>
        /// </summary>
        /// <param name="name">User's name</param>
        /// <param name="password">Uppercase SHA256 of password</param>
        [HttpPut("{name}/token")]
        public JsonResult SignIn(string name, string password)
        {
            var user = FindUser(name);
            if (user == null) return BasicResponse(StatusCodes.Status404NotFound, _configuration.Value.NoTargetUser);
            if (user.Password != password) return BasicResponse(StatusCodes.Status403Forbidden, _configuration.Value.WrongPassword);
            if (user.Disabled) return BasicResponse(StatusCodes.Status403Forbidden, _configuration.Value.DisabledUser);
            var group = Database.UserGroups.FirstOrDefault(e => e.Id == user.UserGroup);
            if (group.Disabled) return BasicResponse(StatusCodes.Status403Forbidden, _configuration.Value.DisabledUser);
            var token = Guid.NewGuid().ToString().ToUpper();
            user.AccessToken = token;
            Verifier.UpdateAccessTime(user);
            Verifier.UpdateAccessAddress(user, HttpContext.Connection.RemoteIpAddress);
            Database.SaveChanges();
            return BasicResponse(data: token);
        }

        /// <summary>
        /// User sign out<br />
        /// <br />
        /// Permission Group
        /// <list type="bullet">
        /// <item><description>NULL</description></item>
        /// </list>
        /// Returned Value
        /// <list type="bullet">
        /// <item><description>NULL</description></item>
        /// </list>
        /// Probable Errors
        /// <list type="bullet">
        /// <item><description>Cannot find current user: 403 [VerifyResult.NonexistentToken]</description></item>
        /// <item><description>Cannot sign out other user: 403 CannotSignOutOthers</description></item>
        /// </list>
        /// </summary>
        /// <param name="name">User's name</param>
        /// <returns></returns>
        [HttpDelete("{name}/token")]
        public JsonResult SignOut(string name)
        {
            var user = FindUser();
            if (user == null) return Basic403NonexistentToken();
            if (!IsNameEqual(user.Name, name)) return BasicResponse(StatusCodes.Status403Forbidden, _configuration.Value.CannotSingOutOthers);
            user.AccessToken = null;
            Verifier.UpdateAccessTime(user);
            Verifier.UpdateAccessAddress(user, HttpContext.Connection.RemoteIpAddress);
            Database.SaveChanges();
            return BasicResponse();
        }

        /// <summary>
        /// Detele user<br />
        /// <br />
        /// Permission Group
        /// <list type="bullet">
        /// <item><description>UserDeletePermission</description></item>
        /// </list>
        /// Returned Value
        /// <list type="bullet">
        /// <item><description>NULL</description></item>
        /// </list>
        /// Probable Errors
        /// <list type="bullet">
        /// <item><description>Permission denied: 403 [VerifyResult]</description></item>
        /// <item><description>Cannot remove self: 403 CannotRemoveSelf</description></item>
        /// <item><description>No target user: 404 NoTargetUser</description></item>
        /// </list>
        /// </summary>
        /// <param name="name">User's name</param>
        [HttpDelete("{name}")]
        public JsonResult Delete(string name)
        {
            var user = FindUser();
            if (!Verify(user, _configuration.Value.UserDeletePermission, out var verifyResult)) return Basic403(verifyResult);
            if (IsNameEqual(user.Name, name)) return BasicResponse(StatusCodes.Status403Forbidden, _configuration.Value.CannotRemoveSelf);
            user = FindUser(name);
            if (user == null) return BasicResponse(StatusCodes.Status404NotFound, _configuration.Value.NoTargetUser);
            user.Disabled = true;
            Database.SaveChanges();
            return BasicResponse();
        }

        /// <summary>
        /// Modify user's information<br />
        /// <br />
        /// Permission Group
        /// <list type="bullet">
        /// <item><description>UserResetPasswordPermission (only for change password)</description></item>
        /// <item><description>UserChangeGroupPermission (only for change usergroup)</description></item>
        /// <item><description>UserModifyPermission (only for change multiple address sign in)</description></item>
        /// <item><description>UserDisablePermission (only for change disabled)</description></item>
        /// </list>
        /// Returned Value
        /// <list type="bullet">
        /// <item><description>{password?:bool, group?:bool, allowMulti?:bool, disabled?:bool}</description></item>
        /// </list>
        /// Probable Errors
        /// <list type="bullet">
        /// <item><description>Permission denied: 403 [VerifyResult]</description></item>
        /// <item><description>Cannot change self's usergroup: 403 CannotChangeSelfGroup</description></item>
        /// <item><description>Cannot disable or enable self: 403 CannotDisableSelf</description></item>
        /// <item><description>No target user: 404 NoTargetUser</description></item>
        /// <item><description>No target usergroup: 404 NoTargetGroup</description></item>
        /// </list>
        /// </summary>
        /// <param name="name">Target user's name</param>
        /// <param name="parameter">
        /// Parameters<br />
        /// <list type="bullet">
        /// <item><description>password: new password (optional, or let it empty to reset password)</description></item>
        /// <item><description>group: new usergroup (optional)</description></item>
        /// <item><description>allowMulti: new value of allow multiple address (optional)</description></item>
        /// <item><description>disabled: new value of disabled (optional)</description></item>
        /// </list>
        /// </param>
        [HttpPatch("{name}")]
        public JsonResult ChangeUserInformation(string name, [FromBody] Hashtable parameter)
        {
            var currentUser = FindUser();
            if (currentUser == null) return Basic403NonexistentToken();
            var targetUser = FindUser(name);
            if (targetUser == null) return BasicResponse(StatusCodes.Status404NotFound, _configuration.Value.NoTargetUser);
            var finalData = new JObject();
            if (parameter.ContainsKey("password"))
            {
                if (currentUser != targetUser)
                {
                    if (!Verify(currentUser, _configuration.Value.UserResetPasswordPermission, out var verifyResult)) return Basic403(verifyResult, finalData);
                    targetUser.Password = _configuration.Value.DefaulPasswordHash;
                }
                else
                    targetUser.Password = parameter["password"].ToString();
                finalData.Add("password", true);
            }
            if (parameter.ContainsKey("group"))
            {
                if (currentUser == targetUser)
                    return BasicResponse(StatusCodes.Status403Forbidden, _configuration.Value.CannotChangeSelfGroup, finalData);
                if (!Verify(currentUser, _configuration.Value.UserChangeGroupPermission, out var verifyResult)) return Basic403(verifyResult, finalData);
                var group = Database.UserGroups.FirstOrDefault(e => e.Name == parameter["group"].ToString());
                if (group == null) return BasicResponse(StatusCodes.Status404NotFound, _configuration.Value.NoTargetGroup, finalData);
                targetUser.UserGroupNavigation = group;
                finalData.Add("group", true);
            }
            if (parameter.ContainsKey("allowMulti"))
            {
                if (currentUser != targetUser && !Verify(currentUser, _configuration.Value.UserModifyPermission, out var verifyResult))
                    return Basic403(verifyResult, finalData);
                targetUser.AllowMultiAddressLogin = (bool) parameter["allowMulti"];
                finalData.Add("allowMulti", true);
            }
            if (!parameter.ContainsKey("disabled")) return BasicResponse(data: finalData);
            {
                if (currentUser == targetUser)
                    return BasicResponse(StatusCodes.Status403Forbidden, _configuration.Value.CannotDisableSelf, finalData);
                if (!Verify(currentUser, _configuration.Value.UserDisablePermission, out var verifyResult))
                    return Basic403(verifyResult, finalData);
                targetUser.Disabled = (bool)parameter["allowMulti"];
                finalData.Add("disabled", true);
            }
            return BasicResponse(data: finalData);
        }

        private string QueryGenerator(UserSearchFilter filter, ICollection<object> param)
        {
            //? MySql connector for .net core still does not support Take() and Skip() in this version
            //? which means we can only form SQL query manually
            //? Also, LIMIT in mysql has significant performnce issue so we will not use LIMIT
            var condition = new List<string>();
            var paramCount = -1;
            if (!string.IsNullOrEmpty(filter.Name))
            {
                condition.Add("Name LIKE concat('%',@p" + ++paramCount + ",'%')");
                param.Add(filter.Name);
            }
            if (!string.IsNullOrEmpty(filter.Group))
            {
                var group = Database.UserGroups.FirstOrDefault(e => e.Name == filter.Group);
                if (group != null)
                {
                    condition.Add("UserGroup = @p" + ++paramCount);
                    param.Add(group.Id);
                }
            }
            if (filter.Disabled.HasValue)
            {
                condition.Add("Disabled = @p" + ++paramCount);
                param.Add(filter.Disabled.Value ? 1 : 0);
            }
            var query = "";
            if (condition.Count > 0) query = " WHERE " + string.Join(" AND ", condition);
            if (filter.Skip.HasValue && filter.Skip.Value > 0)
            {
                query = "SELECT * FROM User WHERE ID >= (SELECT ID FROM User" + query + " ORDER BY ID LIMIT @p" + ++paramCount +
                        ",1)";
                param.Add(filter.Skip.Value);
            }
            else
                query = "SELECT * FROM User" + query;
            if (filter.Take.HasValue)
            {
                query += " LIMIT @p" + ++paramCount;
                param.Add(filter.Take.Value);
            }
            return query;
        }

        /// <summary>
        /// Get user's total count<br />
        /// <br />
        /// Permission Group
        /// <list type="bullet">
        /// <item><description>NULL</description></item>
        /// </list>
        /// Returned Value
        /// <list type="bullet">
        /// <item><description>{count:int}</description></item>
        /// </list>
        /// Probable Errors
        /// <list type="bullet">
        /// <item><description>NULL</description></item>
        /// </list>
        /// </summary>
        /// <param name="filter">Search filter</param>
        [HttpGet(".count")]
        public JsonResult GetUserCount(UserSearchFilter filter)
        {
            var param = new List<object>();
            var query = QueryGenerator(filter, param);
            return BasicResponse(data: Database.Users.FromSql(query, param.ToArray()).Count());
        }

        /// <summary>
        /// Get list of users<br />
        /// <br />
        /// Permission Group
        /// <list type="bullet">
        /// <item><description>UserManagePermission</description></item>
        /// </list>
        /// Returned Value
        /// <list type="bullet">
        /// <item><description>[{name, group, accessTime, accessAddress, allowMulti, disabled:bool, update}]</description></item>
        /// </list>
        /// Probable Errors
        /// <list type="bullet">
        /// <item><description>NULL</description></item>
        /// </list>
        /// </summary>
        /// <param name="filter">Search filter</param>
        /// <returns></returns>
        [HttpGet(".list")]
        public JsonResult GetUserList(UserSearchFilter filter)
        {
            var user = FindUser();
            if (!Verify(user, _configuration.Value.UserManagePermission, out var verifyResult)) return Basic403(verifyResult);
            var param = new List<object>();
            var query = QueryGenerator(filter, param);
            return BasicResponse(data: Database.Users.FromSql(query, param.ToArray()).Select(e => new Hashtable
            {
                {"name", user.Name},
                {"group", user.UserGroupNavigation.Name},
                {"accessTime", user.LastAccessTime},
                {"accessAddress", user.LastAccessAddress},
                {"allowMulti", user.AllowMultiAddressLogin},
                {"disabled", user.Disabled},
                {"update", user.LastUpdate}
            }).ToArray());
        }
    }
}