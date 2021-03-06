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
using EKIFVK.ChemicalLab.Filters;
using EKIFVK.ChemicalLab.Configurations;
using EKIFVK.ChemicalLab.Services.Authentication;
using EKIFVK.ChemicalLab.Services.Tracking;

namespace EKIFVK.ChemicalLab.Controllers {
    /// <summary>
    /// API for UserGroup Management
    /// <list type="bullet">
    /// <item><description>GET /{name} => GetInfo</description></item>
    /// <item><description>POST /{name} => Add</description></item>
    /// <item><description>DELETE /{name} => Disable</description></item>
    /// <item><description>PATCH /{name} => ChangeGroupInformation</description></item>
    /// <item><description>GET /.count => GetGroupCount</description></item>
    /// <item><description>GET /.list => GetGroupList</description></item>
    /// </list>
    /// </summary>
    [Route("api/v1/usergroup")]
    public class UserGroupController : BasicVerifiableController {
        private readonly IOptions<UserModuleConfiguration> _setting;

        public UserGroupController(ChemicalLabContext database, IAuthentication verifier, ITrackService tracker,
            IOptions<UserModuleConfiguration> setting)
            : base(database, verifier, tracker) {
            _setting = setting;
        }

        /// <summary>
        /// Get group information<br />
        /// <br />
        /// Permission Group
        /// <list type="bullet">
        /// <item><description>NULL</description></item>
        /// </list>
        /// Returned Value
        /// <list type="bullet">
        /// <item><description>{n, d, p, u:int}</description></item>
        /// <item><description>n: name</description></item>
        /// <item><description>d: note</description></item>
        /// <item><description>p: permission</description></item>
        /// <item><description>u: user's count of this group</description></item>
        /// </list>
        /// Probable Errors
        /// <list type="bullet">
        /// <item><description>No target group: 404 NoTargetGroup</description></item>
        /// </list>
        /// </summary>
        /// <param name="name">Target user's name</param>
        [HttpGet("{name}")]
        public JsonResult GetInfo(string name) {
            var group = FindGroup(name);
            if (group == null) return BasicResponse(StatusCodes.Status404NotFound, _setting.Value.NoTargetGroup);
            return BasicResponse(data: new Hashtable {
                {"n", group.Name},
                {"d", group.Note},
                {"p", group.Permission},
                {"u", Database.Users.Count(e => e.UserGroup == group.Id)}
            });
        }

        /// <summary>
        /// Add new group<br />
        /// <br />
        /// Permission Group
        /// <list type="bullet">
        /// <item><description>GroupAddingPermission</description></item>
        /// </list>
        /// Returned Value
        /// <list type="bullet">
        /// <item><description>id:int</description></item>
        /// </list>
        /// Probable Errors
        /// <list type="bullet">
        /// <item><description>Permission denied: 403 [VerifyResult]</description></item>
        /// <item><description>Invalid group name format: 400 InvalidGroupNameFormat</description></item>
        /// <item><description>Group already exist: 409 GroupAlreadyExist</description></item>
        /// </list>
        /// </summary>
        /// <param name="name">Group's name (cannot have /\?, first letter cannot be .)</param>
        /// <param name="parameter">
        /// Parameters<br />
        /// <list type="bullet">
        /// <item><description>note: description of this group</description></item>
        /// <item><description>permission: permission of this group</description></item>
        /// </list>
        /// </param>
        [HttpPost("{name}")]
        public JsonResult Add(string name, [FromBody] Hashtable parameter) {
            if (string.IsNullOrEmpty(name) ||
                name.IndexOf("/", StringComparison.Ordinal) > -1 ||
                name.IndexOf("\\", StringComparison.Ordinal) > -1 ||
                name.IndexOf("?", StringComparison.Ordinal) > -1 ||
                name.IndexOf(".", StringComparison.Ordinal) == 0)
                return BasicResponse(StatusCodes.Status400BadRequest, _setting.Value.InvalidGroupNameFormat);
            if (!Verify(Session, _setting.Value.GroupAddingPermission, out var verifyResult))
                return Denied(verifyResult);
            var group = FindGroup(name);
            if (group != null)
                return BasicResponse(StatusCodes.Status409Conflict, _setting.Value.GroupAlreadyExist);
            group = new UserGroup {
                Name = name,
                Note = parameter["note"].ToString(),
                Permission = parameter["permission"].ToString(),
                LastUpdate = DateTime.Now
            };
            Database.UserGroups.Add(group);
            Tracker.Write(new TrackRecord(TrackType.InfoL3, Session, _setting.Value.UserGroupTable, group.Id, "")
                .AddNote(_setting.Value.AddGroup));
            return BasicResponse(data: group.Id);
        }

        /// <summary>
        /// Disable group<br />
        /// <br />
        /// Permission Group
        /// <list type="bullet">
        /// <item><description>GroupModifyDisabledPermission</description></item>
        /// </list>
        /// Returned Value
        /// <list type="bullet">
        /// <item><description>NULL</description></item>
        /// </list>
        /// Probable Errors
        /// <list type="bullet">
        /// <item><description>Permission denied: 403 [VerifyResult]</description></item>
        /// <item><description>No target group: 404 NoTargetGroup</description></item>
        /// <item><description>Cannot disable self's group: 403 CannotDisableSelf</description></item>
        /// </list>
        /// </summary>
        /// <param name="name">Group's name</param>
        [HttpDelete("{name}")]
        public JsonResult Disable(string name) {
            if (!Verify(Session, _setting.Value.GroupModifyDisabledPermission, out var verifyResult))
                return Denied(verifyResult);
            var group = FindGroup(name);
            if (group == null)
                return BasicResponse(StatusCodes.Status404NotFound, _setting.Value.NoTargetGroup);
            if (Session.UserGroup == group.Id)
                return BasicResponse(StatusCodes.Status403Forbidden, _setting.Value.CannotDisableSelf);
            group.Disabled = true;
            group.LastUpdate = DateTime.Now;
            Tracker.Write(new TrackRecord(TrackType.InfoL1, Session, _setting.Value.UserGroupTable, group.Id, _setting.Value.UserGroupTableDisabled)
                .AddNote(_setting.Value.DisableGroup)
                .AddPreviousData(true)
                .AddNewData(false));
            return BasicResponse();
        }

        /// <summary>
        /// Modify user's information<br />
        /// <br />
        /// Permission Group
        /// <list type="bullet">
        /// <item><description>GroupManagePermission (only for change name or note)</description></item>
        /// <item><description>GroupModifyPermissionPermission (only for change permission)</description></item>
        /// </list>
        /// Returned Value
        /// <list type="bullet">
        /// <item><description>{n?:bool, d?:bool, p?:bool}</description></item>
        /// <item><description>n: is name change success</description></item>
        /// <item><description>d: is note change success</description></item>
        /// <item><description>p: is permission change success</description></item>
        /// </list>
        /// Probable Errors
        /// <list type="bullet">
        /// <item><description>Permission denied: 403 [VerifyResult]</description></item>
        /// <item><description>No target group: 404 NoTargetGroup</description></item>
        /// <item><description>Group already exist: 409 GroupAlreadyExist</description></item>
        /// <item><description>Cannot change disabled of self's group: 403 CannotChangeSelfGroupDisabled</description></item>
        /// </list>
        /// </summary>
        /// <param name="name">Target user's name</param>
        /// <param name="parameter">
        /// Parameters<br />
        /// <list type="bullet">
        /// <item><description>name?: new name</description></item>
        /// <item><description>note?: new note</description></item>
        /// <item><description>permission?: new permission</description></item>
        /// </list>
        /// </param>
        [HttpPatch("{name}")]
        public JsonResult ChangeGroupInformation(string name, [FromBody] Hashtable parameter) {
            if (Session == null) return NonexistentToken();
            var target = FindGroup(name);
            if (target == null) return BasicResponse(StatusCodes.Status404NotFound, _setting.Value.NoTargetGroup);
            var finalData = new JObject();
            if (parameter.ContainsKey("name")) {
                if (!Verify(Session, _setting.Value.GroupManagePermission, out var verifyResult))
                    return Denied(verifyResult, finalData);
                var newName = parameter["name"].ToString();
                if (FindGroup(newName) != null)
                    return BasicResponse(StatusCodes.Status409Conflict, _setting.Value.GroupAlreadyExist, finalData);
                var previous = target.Name;
                target.Name = newName;
                target.LastUpdate = DateTime.Now;
                Tracker.Write(new TrackRecord(TrackType.InfoL1, Session, _setting.Value.UserGroupTable, target.Id, _setting.Value.UserGroupTableName)
                    .AddNote(_setting.Value.ChangeGroupName)
                    .AddPreviousData(previous)
                    .AddNewData(target.Name));
                finalData.Add("n", true);
            }
            if (parameter.ContainsKey("note")) {
                if (!Verify(Session, _setting.Value.GroupManagePermission, out var verifyResult))
                    return Denied(verifyResult, finalData);
                var previous = target.Note;
                target.Note = parameter["note"].ToString();
                target.LastUpdate = DateTime.Now;
                Tracker.Write(new TrackRecord(TrackType.InfoL1, Session, _setting.Value.UserGroupTable, target.Id, _setting.Value.UserGroupTableNote)
                    .AddNote(_setting.Value.ChangeGroupNote)
                    .AddPreviousData(previous)
                    .AddNewData(target.Note));
                finalData.Add("d", true);
            }
            if (parameter.ContainsKey("permission")) {
                if (!Verify(Session, _setting.Value.GroupModifyPermissionPermission, out var verifyResult))
                    return Denied(verifyResult, finalData);
                var previous = target.Permission;
                target.Permission = parameter["permission"].ToString();
                target.LastUpdate = DateTime.Now;
                Tracker.Write(new TrackRecord(TrackType.InfoL1, Session, _setting.Value.UserGroupTable, target.Id, _setting.Value.UserGroupTablePermission)
                    .AddNote(_setting.Value.ChangeGroupPermission)
                    .AddPreviousData(previous)
                    .AddNewData(target.Permission));
                finalData.Add("p", true);
            }
            if (!parameter.ContainsKey("disabled"))
                return BasicResponse(data: finalData);
            {
                if (FindGroup(Session) == target)
                    return BasicResponse(StatusCodes.Status403Forbidden, _setting.Value.CannotChangeSelfGroupDisabled, finalData);
                if (!Verify(Session, _setting.Value.GroupModifyDisabledPermission, out var verifyResult))
                    return Denied(verifyResult, finalData);
                var previous = target.Disabled;
                target.Disabled = (bool) parameter["disabled"];
                target.LastUpdate = DateTime.Now;
                Tracker.Write(new TrackRecord(TrackType.InfoL1, Session, _setting.Value.UserGroupTable, target.Id, _setting.Value.UserGroupTableDisabled)
                    .AddNote(_setting.Value.ChangeGroupDisabled)
                    .AddPreviousData(previous)
                    .AddNewData(target.Permission));
                finalData.Add("r", true);
            }
            return BasicResponse(data: finalData);
        }

        /// <summary>
        /// Get groups' total count<br />
        /// <br />
        /// Permission Group
        /// <list type="bullet">
        /// <item><description>NULL</description></item>
        /// </list>
        /// Returned Value
        /// <list type="bullet">
        /// <item><description>count:int</description></item>
        /// </list>
        /// Probable Errors
        /// <list type="bullet">
        /// <item><description>NULL (all illegal parameters will be ignored)</description></item>
        /// </list>
        /// </summary>
        /// <param name="filter">Search filter</param>
        [HttpGet(".count")]
        public JsonResult GetGroupCount(GroupSearchFilter filter) {
            var param = new List<object>();
            var query = QueryGenerator(filter, param);
            return BasicResponse(data: Database.UserGroups.FromSql(query, param.ToArray()).Count());
        }

        /// <summary>
        /// Get list of groups<br />
        /// <br />
        /// Permission Group
        /// <list type="bullet">
        /// <item><description>GroupManagePermission</description></item>
        /// </list>
        /// Returned Value
        /// <list type="bullet">
        /// <item><description>[{n, d, p, u:int}]</description></item>
        /// <item><description>n: name</description></item>
        /// <item><description>d: note</description></item>
        /// <item><description>p: permission</description></item>
        /// <item><description>u: user's count of this group</description></item>
        /// </list>
        /// Probable Errors
        /// <list type="bullet">
        /// <item><description>NULL (all illegal parameters will be ignored)</description></item>
        /// </list>
        /// </summary>
        /// <param name="filter">Search filter</param>
        /// <returns></returns>
        [HttpGet(".list")]
        public JsonResult GetGroupList(GroupSearchFilter filter) {
            if (!Verify(Session, _setting.Value.GroupManagePermission, out var verifyResult))
                return Denied(verifyResult);
            var param = new List<object>();
            var query = QueryGenerator(filter, param);
            return BasicResponse(data: Database.UserGroups.FromSql(query, param.ToArray()).Select(e => new Hashtable {
                {"n", e.Name},
                {"d", e.Note},
                {"p", e.Permission},
                {"u", Database.Users.Count(u => u.UserGroup == e.Id)}
            }).ToArray());
        }

        private static string QueryGenerator(GroupSearchFilter filter, ICollection<object> param) {
            //? MySql connector for .net core still does not support Take() and Skip() in this version
            //? which means we can only form SQL query manually
            //? Also, LIMIT in mysql has significant performnce issue so we will not use LIMIT
            var condition = new List<string>();
            var paramCount = -1;
            if (!string.IsNullOrEmpty(filter.Name)) {
                condition.Add("Name LIKE CONCAT('%',@p" + ++paramCount + ",'%')");
                param.Add(filter.Name);
            }
            if (filter.Disabled.HasValue) {
                condition.Add("Disabled = @p" + ++paramCount);
                param.Add(filter.Disabled.Value ? 1 : 0);
            }
            var query = "";
            if (condition.Count > 0) query = string.Join(" AND ", condition);
            if (filter.Skip.HasValue && filter.Skip.Value > 0) {
                query = "SELECT * FROM UserGroup WHERE ID >= (SELECT ID FROM UserGroup WHERE " + query +
                        " ORDER BY ID LIMIT @p" + ++paramCount +
                        ",1)" + (query.Length > 0 ? " AND " : "") + query;
                param.Add(filter.Skip.Value);
            }
            else
                query = "SELECT * FROM UserGroup WHERE " + query;
            if (filter.Take.HasValue) {
                query += " LIMIT @p" + ++paramCount;
                param.Add(filter.Take.Value);
            }
            return query;
        }
    }
}