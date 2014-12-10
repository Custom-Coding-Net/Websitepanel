﻿using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mail;
using System.Text;
using System.Text.RegularExpressions;
using WebsitePanel.Providers.DomainLookup;
using Whois.NET;

namespace WebsitePanel.EnterpriseServer
{
    public class DomainExpirationTask: SchedulerTask
    {
        private static readonly string TaskId = "SCHEDULE_TASK_DOMAIN_EXPIRATION";

        // Input parameters:
        private static readonly string DaysBeforeNotify = "DAYS_BEFORE";
        private static readonly string MailToParameter = "MAIL_TO";
        private static readonly string EnableNotification = "ENABLE_NOTIFICATION";


        private static readonly string MailBodyTemplateParameter = "MAIL_BODY";
        private static readonly string MailBodyDomainRecordTemplateParameter = "MAIL_DOMAIN_RECORD";

        public override void DoWork()
        {
            BackgroundTask topTask = TaskManager.TopTask;
            var domainUsers = new Dictionary<int, UserInfo>();

            // get input parameters
            int daysBeforeNotify;
            bool sendEmailNotifcation = Convert.ToBoolean( topTask.GetParamValue(EnableNotification));

            // check input parameters
            if (String.IsNullOrEmpty((string)topTask.GetParamValue("MAIL_TO")))
            {
                TaskManager.WriteWarning("The e-mail message has not been sent because 'Mail To' is empty.");

                return;
            }

            int.TryParse((string)topTask.GetParamValue(DaysBeforeNotify), out daysBeforeNotify);

            var user = UserController.GetUser(topTask.EffectiveUserId);

            var packages = GetUserPackages(user.UserId, user.Role);

            var expiredDomains = new List<DomainInfo>();

            foreach (var package in packages)
            {
                var domains = ServerController.GetDomains(package.PackageId);

                domains = domains.Where(x => !x.IsSubDomain && !x.IsDomainPointer).ToList(); //Selecting top-level domains

                domains = domains.Where(x => x.CreationDate == null || x.ExpirationDate == null ? true : CheckDomainExpiration(x.ExpirationDate, daysBeforeNotify)).ToList(); // selecting expired or with empty expire date domains

                var domainUser = UserController.GetUser(package.UserId);

                if (!domainUsers.ContainsKey(package.PackageId))
                {
                    domainUsers.Add(package.PackageId, domainUser);
                }

                foreach (var domain in domains)
                {
                    ServerController.UpdateDomainRegistrationData(domain);

                    if (CheckDomainExpiration(domain.ExpirationDate, daysBeforeNotify))
                    {
                        expiredDomains.Add(domain);
                    }
                }
            }

            expiredDomains = expiredDomains.GroupBy(p => p.DomainId).Select(g => g.First()).ToList();

            if (expiredDomains.Count > 0 && sendEmailNotifcation)
            {
                SendMailMessage(user, expiredDomains, domainUsers);
            }
        }

        private IEnumerable<PackageInfo> GetUserPackages(int userId,UserRole userRole)
        {
            var packages = new List<PackageInfo>();

            switch (userRole)
            {
                case UserRole.Administrator:
                {
                    packages = ObjectUtils.CreateListFromDataReader<PackageInfo>(DataProvider.GetAllPackages());
                    break;
                }
                default:
                {
                    packages = PackageController.GetMyPackages(userId);
                    break; 
                }
            }

            return packages;
        }

        private bool CheckDomainExpiration(DateTime? date, int daysBeforeNotify)
        {
            if (date == null)
            {
                return false;
            }

            return (date.Value - DateTime.Now).Days < daysBeforeNotify;
        }

        private void SendMailMessage(UserInfo user, IEnumerable<DomainInfo> domains, Dictionary<int, UserInfo> domainUsers)
        {
            BackgroundTask topTask = TaskManager.TopTask;

            UserSettings settings = UserController.GetUserSettings(user.UserId, UserSettings.DOMAIN_EXPIRATION_LETTER);

            string from = settings["From"];

            var bcc = settings["CC"];

            string subject = settings["Subject"];
            string body = user.HtmlMail ? settings["HtmlBody"] : settings["TextBody"];
            bool isHtml = user.HtmlMail;

            MailPriority priority = MailPriority.Normal;
            if (!String.IsNullOrEmpty(settings["Priority"]))
                priority = (MailPriority)Enum.Parse(typeof(MailPriority), settings["Priority"], true);

            // input parameters
            string mailTo = (string)topTask.GetParamValue("MAIL_TO");

            Hashtable items = new Hashtable();

            items["user"] = user;
            items["Domains"] = domains.Select(x => new { DomainName = x.DomainName, 
                                                         ExpirationDate = x.ExpirationDate, 
                                                         Customer = string.Format("{0} {1}", domainUsers[x.PackageId].FirstName, domainUsers[x.PackageId].LastName) });

            body = PackageController.EvaluateTemplate(body, items);

            // send mail message
            MailHelper.SendMessage(from, mailTo, bcc, subject, body, priority, isHtml);
        }
    }
}