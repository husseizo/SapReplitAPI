using System;
using Quartz;
using Microsoft.Extensions.DependencyInjection;

namespace SapReplitAPI.Extensions
{
    public static class QuartzExtensions
    {
        public static void AddJobAndTrigger<T>(
            this IServiceCollectionQuartzConfigurator q,
            string jobName,
            TimeSpan interval
        ) where T : IJob
        {
            var jobKey = new JobKey(jobName);

            q.AddJob<T>(opts => opts.WithIdentity(jobKey));

            q.AddTrigger(opts => opts
                .ForJob(jobKey)
                .WithIdentity($"{jobName}-trigger")
                .WithSimpleSchedule(x => x.WithInterval(interval).RepeatForever()));
        }
    }
}