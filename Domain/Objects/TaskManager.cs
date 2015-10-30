﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Configuration;
using System.Data.SqlClient;
using Dapper;

namespace Domain
{
    public static class TaskManager
    {
        private static readonly string ConnectionString = ConfigurationManager.AppSettings.Get("Connection");
        public static Task[] GetUserTasks(int UserId)
        {
           
            var tsk = new List<Task>();
            AdminUser adminuser = new AdminUser();
            tsk.AddRange(GetTasks(DateTime.Now, UserId));
            using (var db = new SqlConnection(ConnectionString))
            {
                var q = @"IF EXISTS(SELECT* FROM [collect2000].[ERCTasks].[Users] WHERE UserRole = 'admin' AND UserId = @UserId)  

                            SELECT * FROM [collect2000].[ERCTasks].[TaskAdminUsers] WHERE AdminUserId = @UserId";
                var admuser = db.Query<AdminUser>(q, new { UserId = UserId });
                foreach (var uid in admuser)
                {
                    var temptask = GetTasks(DateTime.Now, uid.UserId);
                    foreach (var item in temptask)
                    {

                        if (tsk.Select(s => s.TaskId == item.TaskId).Count() == 0)
                        {

                            tsk.AddRange(temptask.Where(p => p.TaskId == item.TaskId));
                        }
                    }

                }
            }
            return tsk.ToArray<Task>();


           // return tasks.Where(p => p.AssignedTo.Any(k => k.UserId == UserId)).ToArray<TaskInfo>();
        }
        public static void MarkTaskComplete(int TaskId, int UserId)
        {
            var dueTasks = GetTasks(DateTime.Now, UserId);
            // is the task still due? are we early?
            var task = dueTasks.Where(p => p.TaskId == TaskId).FirstOrDefault();
            SLALogManager.SLALogEntry(new SLALog
            {
                TaskId = TaskId,
                SLAMet = task != null,
                SubmittedByUserId = UserId,
                SubmittedTime = DateTime.Now
            });
        }
        public static bool IsBusinessDay(DateTime day)
        {
            return day.DayOfWeek != DayOfWeek.Sunday;
        }
        public  static TaskInfo GetTaskInfo(int  TaskId)
        {
            var taskInfo = new TaskInfo();
            using (var db = new SqlConnection(ConnectionString))
            {
                // get the task tags
                var q = @"SELECT Tags.* FROM [collect2000].[ERCTasks].[TaskTags] 
                            JOIN [collect2000].[ERCTasks].[Tags] ON Tags.TagId = TaskTags.TagId
                            WHERE TaskTags.TaskId = @taskId";
                var tags = db.Query<Tag>(q, new { taskId = TaskId });
                taskInfo.tags = tags.ToArray();
                
                // get the users assigned this task
                q = @"SELECT ETU.* FROM [Collect2000].[ERCTasks].[TaskUsers]
                        JOIN [collect2000].[ERCTasks].[Users] ETU ON ETU.UserId = TaskUsers.UserId
                        WHERE TaskUsers.TaskId = @taskId";
                var users = db.Query<User>(q, new { taskId = TaskId }).ToArray<User>();
                taskInfo.AssignedTo = users;
            }
            return taskInfo;
        }

        private static int GetMonthlyIndex(DateTime today)
        {
            // start from the start of the month
            // only count business days
            var start_date = today.AddDays(-today.Day + 1);
            var inx = 0;
            for (var i = 0; i < DateTime.DaysInMonth(today.Year, today.Month); i++)
            {
                var day = start_date.AddDays(i);
                if (!IsBusinessDay(day)) continue;
                inx++;
                if (today.Day == day.Day) break;
            }
            return inx;
        }
        private static int GetMonthlyNegativeIndex(DateTime today)
        {
            // start from the end of the month
            // count backwards, skipping non-business days
            var daysOfMonth = DateTime.DaysInMonth(today.Year, today.Month);
            var daysLeft = daysOfMonth - today.Day;
            var start_date = today.AddDays(daysLeft);
            var inx = 0;
            for (var i = 0; i < daysOfMonth; i++)
            {
                var day = start_date.AddDays(-i);
                if (!IsBusinessDay(day)) continue;
                inx--;
                if (today.Day == day.Day) break;
            }
            return inx;
        }
        // TODO - Implement caching for this, this method is called a lot
        /// <summary>
        /// Returns the tasks due for the day after the given epoch
        ///now this method return only task list
        ///We try to minimize the record fetching time by passing the userId in query
        ///on page load no need of task information
        ///We fetch information from database by user demand
        /// </summary>
        /// <param name="epoch"></param>
        /// <returns></returns>
        public static Task[] GetTasks(DateTime epoch, int UserId)
        {
            // not a business day, no tasks!
            if (!IsBusinessDay(epoch))
            {
                return new Task[] { };
            }
            using (var db = new SqlConnection(ConnectionString))
            {
                int hourNow = epoch.Hour;
                int minutesNow = epoch.Minute;
                var tasks = new List<Task>();
                db.Open();

                // first get one time tasks with no recurrence defined
                var q = @"SELECT t.*, (SELECT COUNT(*) FROM [Collect2000].[ERCTasks].[SLALOG] WHERE TaskId = t.TaskId) isComplete FROM [Collect2000].[ERCTasks].[Tasks] t WITH(NOLOCK)
                            JOIN [Collect2000].[ERCTasks].[TaskUsers] u WITH(NOLOCK) ON u.TaskId = t.TaskId AND u.UserId = @UserId 
                            WHERE t.TaskDueDate IS NOT NULL AND t.TaskDueDate > @curDate AND CAST(t.TaskDueDate as Date) = CAST(@curDate as DATE)";
                tasks.AddRange(db.Query<Task>(q, new { curDate = epoch, UserId = UserId }));

                // recurrence patterns
                // daily, weekly, monthly
                // examples:
                // 1st business day of every month
                // last business day every week
                // Task.RecurrenceBusinessDayStep th day past the chosen pattern
                // RecurrenceBusinessDayStep can be negative

                // start by the easiest
                // get the tasks for today that still have to be done!

                q = @"SELECT t.*,  (SELECT COUNT(*) FROM [Collect2000].[ERCTasks].[SLALOG] WHERE TaskId = t.TaskId) isComplete FROM [Collect2000].[ERCTasks].[Tasks] t WITH(NOLOCK)
                            JOIN [Collect2000].[ERCTasks].[TaskUsers] u WITH(NOLOCK) ON u.TaskId = t.TaskId AND u.UserId = @UserId 
                            WHERE t.RecurrencePattern = 'Daily' AND t.TaskDueDate IS NULL AND (t.TaskDueHour > @hourNow OR (t.TaskDueMinutes > @minutesNow AND t.TaskDueHour = @hourNow))";
                tasks.AddRange(db.Query<Task>(q, new { hourNow = hourNow, minutesNow = minutesNow, UserId = UserId }));

                var dayOfMonth = epoch.Day;
                var dayOfWeek = (int)epoch.DayOfWeek;

                var daysInMonth = DateTime.DaysInMonth(epoch.Year, epoch.Month);
                var daysInWeek = 6;

                var negativeMonthlyIndex = GetMonthlyNegativeIndex(epoch.Date); //dayOfMonth - daysInMonth - 1;
                var negativeWeeklyIndex = dayOfWeek - daysInWeek - 1;
                var monthlyIndex = GetMonthlyIndex(epoch.Date);
                var weeklyIndex = dayOfWeek;

                // get tasks where day of month matches
                q = @"SELECT t.*,  (SELECT COUNT(*) FROM [Collect2000].[ERCTasks].[SLALOG] WHERE TaskId = t.TaskId) isComplete FROM [Collect2000].[ERCTasks].[Tasks] t WITH(NOLOCK)
                            JOIN [Collect2000].[ERCTasks].[TaskUsers] u WITH(NOLOCK) ON u.TaskId = t.TaskId AND u.UserId = @UserId 
                            WHERE t.RecurrencePattern = 'Monthly' AND t.TaskDueDate IS NULL
                   AND (((t.RecurrenceBusinessDayStep > 0 AND t.RecurrenceBusinessDayStep = @monthlyIndex) OR (t.RecurrenceBusinessDayStep < 0 AND t.RecurrenceBusinessDayStep = @negativeMonthlyIndex)))
                   AND (t.TaskDueHour > @hourNow OR (t.TaskDueMinutes > @minutesNow AND t.TaskDueHour = @hourNow))";
                tasks.AddRange(db.Query<Task>(q, new { hourNow = hourNow, minutesNow = minutesNow, monthlyIndex = monthlyIndex, negativeMonthlyIndex = negativeMonthlyIndex, UserId = UserId }));
                //get tasks where day of week matches
                q = @"SELECT t.*,  (SELECT COUNT(*) FROM [Collect2000].[ERCTasks].[SLALOG] WHERE TaskId = t.TaskId) isComplete FROM [Collect2000].[ERCTasks].[Tasks] t WITH(NOLOCK)
                            JOIN [Collect2000].[ERCTasks].[TaskUsers] u WITH(NOLOCK) ON u.TaskId = t.TaskId AND u.UserId = @UserId 
                        WHERE t.RecurrencePattern = 'Weekly' AND t.TaskDueDate IS NULL
                   AND (((t.RecurrenceBusinessDayStep > 0 AND t.RecurrenceBusinessDayStep = @weeklyIndex) OR (t.RecurrenceBusinessDayStep < 0 AND t.RecurrenceBusinessDayStep = @negativeWeeklyIndex)))
                   AND (t.TaskDueHour > @hourNow OR (t.TaskDueMinutes > @minutesNow AND t.TaskDueHour >= @hourNow))";
                tasks.AddRange(db.Query<Task>(q, new { hourNow = hourNow, minutesNow = minutesNow, dayOfWeek = dayOfWeek, weeklyIndex = weeklyIndex, negativeWeeklyIndex = negativeWeeklyIndex, UserId =  UserId}));

                // now get the task info
                return tasks.ToArray<Task>();
                
            }
        }
        public static int AddTask(Task task)
        {
            using (var db = new SqlConnection(ConnectionString))
            {
                db.Open();
                var q = @"INSERT INTO [collect2000].[ERCTasks].[Tasks] 
                        (TaskDesc, TaskDueDate, TaskDueHour, TaskDueMinutes, RecurrencePattern, RecurrenceBusinessDayStep, TaskName)
                        VALUES (@desc, @duedate, @hour, @minutes, @pattern, @daystep, @TaskName);SELECT SCOPE_IDENTITY();";
                var ret = db.Query<int>(q, new { desc = task.TaskDesc, duedate = task.TaskDueDate,
                        hour = task.TaskDueHour, minutes = task.TaskDueMinutes,
                        pattern = task.RecurrencePattern, daystep = task.RecurrenceBusinessDayStep, TaskName = task.TaskName }).FirstOrDefault<int>();
                return ret;
            }
        }

        public static void AssignUserList(User[] _objuser, int id)
        {
            foreach (var usr in _objuser)
            {
                AssignedToUser(usr.UserId, id);
            }
        }
        private  static void AssignedToUser(int userid, int taskid)
        {
            using (var db = new SqlConnection(ConnectionString))
            {
                db.Open();
                var q = @"IF NOT EXISTS(SELECT * FROM [Collect2000].[ERCTasks].[TaskUsers] WHERE UserId = @UserId AND TaskId = @TaskId)  INSERT INTO [Collect2000].[ERCTasks].[TaskUsers] 
                        (UserId, TaskId)
                        VALUES (@UserId, @TaskId)";
                db.Query<int>(q, new
                {
                    UserId = userid,
                    TaskId = taskid
                    
                });
                
            }
        }
        public static void RemoveTask(int TaskId)
        {
            using (var db = new SqlConnection(ConnectionString))
            {
                db.Open();
                var q = @"DELETE FROM [collect2000].[ERCTasks].[Tasks] WHERE TaskId = @taskId";
                db.Query(q, new { taskId = TaskId });
            }
        }

        public static User[] GetUsers()
        {
            using (var db = new SqlConnection(ConnectionString))
            {
                db.Open();
                // first get one time tasks with no recurrence defined 
                var q = @"SELECT * FROM [collect2000].[ERCTasks].[Users]";
                return db.Query<User>(q).ToArray<User>();

            }
        }

        public static void EditTask(Task task)
        {
            using (var db = new SqlConnection(ConnectionString))
            {
                db.Open();
                var q = @"UPDATE  [collect2000].[ERCTasks].[Tasks] SET  
                        TaskDesc = @desc, TaskDueDate = @duedate, TaskDueHour=@hour,TaskDueMinutes= @minutes, RecurrencePattern=@pattern, RecurrenceBusinessDayStep=@daystep, TaskName=@TaskName Where TaskId=@TaskId";
                var ret = db.Query(q, new
                {
                    desc = task.TaskDesc,
                    duedate = task.TaskDueDate,
                    hour = task.TaskDueHour,
                    minutes = task.TaskDueMinutes,
                    pattern = task.RecurrencePattern,
                    daystep = task.RecurrenceBusinessDayStep,
                    TaskName = task.TaskName,
                    TaskId = task.TaskId
                });

            }
        }

        //send start date and start to get weekly task
        public static List<Task[]> GetWeeklyTaskForCalendar(DateTime startdate, DateTime endDate, int userId)
        {
            List<Task[]> _lst = new List<Task[]>();
            var start_date = Convert.ToDateTime( startdate);
            var end_date = Convert.ToDateTime(endDate);
            var culture = new System.Globalization.CultureInfo("en-US");
            for (var day = 0; day <= 6 ; day++)
            {
                var date = start_date.AddDays(day);
                var tsk = new List<Task>();
                var tasks = Domain.TaskManager.GetTasks(date, userId);
                tsk.AddRange(tasks);
               
                //this code block is added for new requirement
                //if user role is admin
                //the user associtated with admin and thier task is also populate to calendar view 
                using (var db = new SqlConnection(ConnectionString))
                {
                    var q = @"IF EXISTS(SELECT* FROM [collect2000].[ERCTasks].[Users] WHERE UserRole = 'admin' AND UserId = @UserId) 
                            SELECT * FROM [collect2000].[ERCTasks].[TaskAdminUsers] WHERE AdminUserId = @UserId";
                    var admuser = db.Query<AdminUser>(q, new { UserId = userId });
                    foreach (var uid in admuser)
                    {
                        var temptask = GetTasks(date, uid.UserId);
                        foreach (var item in temptask)
                        {
                            if (tsk.Where(p => p.TaskId == item.TaskId).Count() == 0) tsk.AddRange(temptask.Where(p => p.TaskId == item.TaskId));                            
                           
                        }

                    }
                }
                _lst.Add(tsk.ToArray<Task>());
                
            }    
              
            return _lst;
        }
        //get monthly task in month view calendar
        //only call while change in month
        public static List<Task[]> GetPerMonthTaskForCalendar(DateTime startdate, int userId)
        {
            List<Task[]> _lst = new List<Task[]>();
            var start_date = startdate;
            var culture = new System.Globalization.CultureInfo("en-US");
            for (var day = 0; day < DateTime.DaysInMonth(start_date.Year, start_date.Month); day++)
            {
            
                var date = start_date.AddDays(day);
                var tasks = Domain.TaskManager.GetTasks(date, userId);
                var tsk = new List<Task>();               
                tsk.AddRange(tasks);

                //this code block is added for new requirement
                //if user role is admin
                //the user associtated with admin and thier task is also populate to calendar view 
                using (var db = new SqlConnection(ConnectionString))
                {
                    var q = @"IF EXISTS(SELECT* FROM [collect2000].[ERCTasks].[Users] WHERE UserRole = 'admin' AND UserId = @UserId) 
                            SELECT * FROM [collect2000].[ERCTasks].[TaskAdminUsers] WHERE AdminUserId = @UserId";
                    var admuser = db.Query<AdminUser>(q, new { UserId = userId });
                    foreach (var uid in admuser)
                    {
                        var temptask = GetTasks(date, uid.UserId);
                        foreach (var item in temptask)
                        {
                            if (tsk.Where(p => p.TaskId == item.TaskId).Count() == 0) tsk.AddRange(temptask.Where(p => p.TaskId == item.TaskId));

                        }

                    }
                }
                _lst.Add(tsk.ToArray<Task>());
            }
            
            return _lst;
        }

        public static Task[] GetPerDayTasks(DateTime startdate, int userId)
        {
            var task = new List<Task>();
            var tsk = Domain.TaskManager.GetTasks(startdate, userId).ToArray<Task>();
            task.AddRange(tsk);
            //this code block is added for new requirement
            //if user role is admin
            //the user associtated with admin and thier task is also populate to calendar view 
             using (var db = new SqlConnection(ConnectionString))
             {
                 var q = @"IF EXISTS(SELECT* FROM [collect2000].[ERCTasks].[Users] WHERE UserRole = 'admin' AND UserId = @UserId) 
                            SELECT * FROM [collect2000].[ERCTasks].[TaskAdminUsers] WHERE AdminUserId = @UserId";
                 var admuser = db.Query<AdminUser>(q, new { UserId = userId });
                 foreach (var uid in admuser)
                 {
                     var temptask = GetTasks(startdate, uid.UserId);
                     foreach (var item in temptask)
                     {
                         if (task.Where(p => p.TaskId == item.TaskId).Count() == 0) task.AddRange(temptask.Where(p => p.TaskId == item.TaskId).ToArray<Task>());

                     }

                 }
             }
             return task.ToArray<Task>();
        }

        public static void RemoveAssignedUser(int UserId, int TaskId)
        {
            using (var db = new SqlConnection(ConnectionString))
            {
                db.Open();
                var q = @"DELETE FROM [collect2000].[ERCTasks].[TaskUsers] WHERE TaskId = @taskId AND UserId = @UserId";
                db.Query(q, new { taskId = TaskId ,UserId = UserId });
            }
        }

        // Import data to [Collect2000].[ERCTasks].[Tasks] and reference table, [Collect2000].[ERCTasks].[TaskUsers], [Collect2000].[ERCTasks].[Users]
        // Involved table for data migration [Collect2000].[dbo].[erc_ff_FileProcessing], 
        public static void ImportDataDailyRecurrenceTask()
        {
            //[Collect2000].[dbo].erc_ff_FileProcessingData.Frequency should map with [Collect2000].[ERCTasks].[Tasks].RecurrencePattern
	        //[Collect2000].[dbo].erc_ff_FileProcessingData.Recurrance should map with [Collect2000].[ERCTasks].[Tasks].RecurrencePattern.RecurrenceBusinessDayStep
	        //Last 4 characters of [Collect2000].[dbo].erc_ff_FileProcessingData.ProcessorSLA should map with [Collect2000].[ERCTasks].[Tasks].TaskDueMinutes
	        //Left characters of  (Total length of [Collect2000].[dbo].erc_ff_FileProcessingData.ProcessorSLA - 4) should map with 	[Collect2000].[ERCTasks].[Tasks].TaskDueHour
	        //[Collect2000].[ERCTasks].[Tasks].TaskDueDate should be null in case of daily recurrence.
	        //We think the [Collect2000].[ERCTasks].[Tasks].TaskName should come from [Collect2000].[dbo].erc_ff_FpProcessorType on behalf of [Collect2000].[dbo].[erc_ff_FileProcessing].ProcessorType.
	        // We think the [Collect2000].[ERCTasks].[Tasks].TaskDesc will remain blank because we don't get the matched column in [Collect2000].[dbo].[erc_ff_FileProcessing].
	        //Create User [Collect2000].[ERCTasks].[Users]. We assume that user is [Collect2000].[dbo].[erc_ff_FileProcessing].Processor. 
	        //We verify user using  UserManager.GetUserDetails 
			//For task assignment, UserId & TaskId from above steps to be inserted into  [Collect2000].[ERCTasks].[TaskUsers].
	        // [Collect2000].[dbo].erc_ff_FileProcessingData is the child table of [Collect2000].[dbo].[erc_ff_FileProcessing],
	        //We don't think data require from this [Collect2000].[dbo].erc_ff_FileProcessingData.
            //Following condition should meet 
            // TaskDueDate = NULL, TaskDueHour > 0, TaskDueMinutes > 0(conditional) and belong to login user            
            //Temporary using TaskDesc to store the username
            //We assume that need to assign the task to admin by default, if it does not require then remove this part
            var Dailytasks = new List<Task>();
            int userId = 0;//to store recent inserted userId, TaskDueHour > 0
            using (var db = new SqlConnection(ConnectionString))
            {
                var q = @"SELECT  DISTINCT  top 2
			                ff.Processor TaskName,
			                NULL TaskDueDate,
			                CASE WHEN LEN(tt.ProcessorSLA) > 4 THEN
				            LEFT(tt.ProcessorSLA, LEN(tt.ProcessorSLA) - 4)
			                    ELSE NULL END TaskDueHour,
			                    CASE WHEN LEN(tt.ProcessorSLA) > 4 THEN
				                LEFT(RIGHT(tt.ProcessorSLA,4),2)
				                ELSE NULL END TaskDueMinutes,
			                tt.Frequency RecurrencePattern,
			                tt.Recurrance RecurrenceBusinessDayStep,
			                tt.Processor TaskDesc,
                            tt.Customer
			            FROM [Collect2000].[dbo].[erc_ff_FileProcessing] tt
                        JOIN [Collect2000].[dbo].erc_ff_FpProcessorType ff WITH(NOLOCK) ON ff.pid = tt.ProcessorType
			                WHERE tt.Frequency = 'Daily'";
                Dailytasks.AddRange(db.Query<Task>(q));
                foreach (var dr in Dailytasks)
                {
                    userId = 0; 
                        var q1 = @"INSERT INTO [collect2000].[ERCTasks].[Tasks] 
                        (TaskDesc, TaskDueDate, TaskDueHour, TaskDueMinutes, RecurrencePattern, RecurrenceBusinessDayStep, TaskName, Customer)
                        VALUES (@TaskName, @duedate, @hour, @minutes, @pattern, @daystep, @TaskName, @Customer);SELECT SCOPE_IDENTITY();";
                        var Tid = db.Query<int>(q1, new
                        {
                            
                            duedate = dr.TaskDueDate,
                            hour = dr.TaskDueHour,
                            minutes = dr.TaskDueMinutes,
                            pattern = dr.RecurrencePattern,
                            daystep = dr.RecurrenceBusinessDayStep,
                            TaskName = dr.TaskName,
                            Customer = dr.Customer
                        }).FirstOrDefault<int>();

                        User usr = UserManager.GetUserDetails(dr.TaskDesc);//dr.TaskDesc = [Collect2000].[dbo].[erc_ff_FileProcessing].Processor. We don't have variable for username in Task class so utilized the un-used TaskDesc temporary.
                        //create user by [Collect2000].[dbo].[erc_ff_FileProcessing].Processor
                        if (usr.UserName != null && usr.Email != null)
                        {
                            userId = db.ExecuteScalar<int>(
                               "IF NOT EXISTS(SELECT * FROM [collect2000].[ERCTasks].[Users] WHERE UserName = @userName) BEGIN  INSERT INTO [collect2000].[ERCTasks].[Users] (UserName, DisplayName, Email) VALUES (@userName, @dispName, @email); SELECT SCOPE_IDENTITY(); END IF EXISTS(SELECT * FROM [collect2000].[ERCTasks].[Users] WHERE UserName = @userName) SELECT UserId FROM [collect2000].[ERCTasks].[Users] WHERE UserName = @userName",
                               new { userName = usr.UserName, dispName = usr.DisplayName, email = usr.Email });

                        }                       

                    //assignment of current created task
                    //in above query we pay attention on if user exist then return the exist userId otherwise new inserted userId will return
                    //if UserName and Email is null thenin this case UserId will zero because code will not execute.
                    //We have provided the below codition to avoid assignment if UseId is zero.
                     if (userId > 0) AssignedToUser(userId, Tid);
                    //default assign to admin
                    //commented the auto assign to admin for task "Modify ImportDataDailyRecurrenceTask #15"
                    //in future if we need auto assign to admin then simply uncomment the code
                     //   userId = db.ExecuteScalar<int>(
                      //            "SELECT TOP 1 UserId FROM [collect2000].[ERCTasks].[Users] WHERE UserRole = 'admin'",
                      //            new { userName = usr.UserName, dispName = usr.DisplayName, email = usr.Email });
                      //  AssignedToUser(userId, Tid);
                }

            }
        }
        //Function to save checked off Users for track by admin      
        public static void SaveAdminUsers(string CheckedUser, int userId)
        {
            using (var db = new SqlConnection(ConnectionString))
            {
                db.Open();
                var q = @"DELETE FROM [collect2000].[ERCTasks].[TaskAdminUsers] WHERE AdminUserId  = @userId";
                db.Query(q, new { userId = userId });
                if (CheckedUser != "NULL")
                {
                    string[] usr = CheckedUser.Split(',');
                    foreach (string item in usr)
                    {
                        var q1 = @"INSERT INTO [collect2000].[ERCTasks].[TaskAdminUsers]
                        (AdminUserId, UserId)
                        VALUES (@userId,@chekedUserid);";
                        var Tid = db.Query<int>(q1, new
                        {
                            userId = userId,
                            chekedUserid = item
                        });
                    }
                }


            }
        }
        //Function to get checked off Users 
        public static AdminUser[] GetAdminUsers(int userId)
        {
            var adminusr=new List<AdminUser>();
            using (var db = new SqlConnection(ConnectionString))
            {
                var q = @"SELECT * FROM [collect2000].[ERCTasks].[TaskAdminUsers] WHERE AdminUserId = @UserId";
                var admuser = db.Query<AdminUser>(q, new { UserId = userId });
                return admuser.ToArray();
            }
           
        }
    }
}
