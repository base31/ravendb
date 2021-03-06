﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Raven.Abstractions.Data;
using Raven.Abstractions.Extensions;
using Raven.Abstractions.Logging;
using Raven.Abstractions.Util;
using Raven.Database.Server;
using Raven.Database.Util;

namespace Raven.Database.Indexing
{
	public class DefaultBackgroundTaskExecuter : IBackgroundTaskExecuter
	{
		private static readonly ILog logger = LogManager.GetCurrentClassLogger();

		public IList<TResult> Apply<T, TResult>(WorkContext context, IEnumerable<T> source, Func<T, TResult> func)
			where TResult : class
		{
			if (context.Configuration.MaxNumberOfParallelIndexTasks == 1)
			{
				return source.Select(func).ToList();
			}

			return source.AsParallel()
				.Select(func)
				.Where(x => x != null)
				.ToList();
		}

		private readonly AtomicDictionary<Tuple<Timer, ConcurrentSet<IRepeatedAction>>> timers =
			new AtomicDictionary<Tuple<Timer, ConcurrentSet<IRepeatedAction>>>();

		public void Repeat(IRepeatedAction action)
		{
			var tuple = timers.GetOrAdd(action.RepeatDuration.ToString(),
												  span =>
												  {
													  var repeatedActions = new ConcurrentSet<IRepeatedAction>
			                                      	{
			                                      		action
			                                      	};
													  var timer = new Timer(ExecuteTimer, action.RepeatDuration,
																			action.RepeatDuration,
																			action.RepeatDuration);
													  return Tuple.Create(timer, repeatedActions);
												  });
			tuple.Item2.TryAdd(action);
		}

		private void ExecuteTimer(object state)
		{
			var span = state.ToString();
			Tuple<Timer, ConcurrentSet<IRepeatedAction>> tuple;
			if (timers.TryGetValue(span, out tuple) == false)
				return;

			foreach (var repeatedAction in tuple.Item2)
			{
				if (repeatedAction.IsValid == false)
					tuple.Item2.TryRemove(repeatedAction);

				try
				{
					repeatedAction.Execute();
				}
				catch (Exception e)
				{
					logger.ErrorException("Could not execute repeated task", e);
				}
			}

			if (tuple.Item2.Count != 0)
				return;

			if (timers.TryRemove(span, out tuple) == false)
				return;

			tuple.Item1.Dispose();
		}

		/// <summary>
		/// Note that here we assume that  source may be very large (number of documents)
		/// </summary>
		public void ExecuteAllBuffered<T>(WorkContext context, IList<T> source, Action<IEnumerator<T>> action)
		{
			const int bufferSize = 256;
			var maxNumberOfParallelIndexTasks = context.Configuration.MaxNumberOfParallelIndexTasks;
			if (maxNumberOfParallelIndexTasks == 1 || source.Count <= bufferSize)
			{
				using (var e = source.GetEnumerator())
					action(e);
				return;
			}
			var steps = source.Count/maxNumberOfParallelIndexTasks;
			Parallel.For(0, steps,
			             new ParallelOptions {MaxDegreeOfParallelism = maxNumberOfParallelIndexTasks},
			             i => action(Yield(source, i*bufferSize, bufferSize)));
		}

		private IEnumerator<T> Yield<T>(IList<T> source, int start, int end)
		{
			while (start < source.Count && end > 0)
			{
				end--;
				yield return source[start];
				start++;
			}
		}

		/// <summary>
		/// Note that we assume that source is a relatively small number, expected to be 
		/// the number of indexes, not the number of documents.
		/// </summary>
		public void ExecuteAll<T>(
			WorkContext context,
			IList<T> source, Action<T, long> action)
		{
			if (context.Configuration.MaxNumberOfParallelIndexTasks == 1)
			{
				long i = 0;
				foreach (var item in source)
				{
					action(item, i++);
				}
				return;
			}
			context.CancellationToken.ThrowIfCancellationRequested();
			var partitioneds = Partition(source, context.Configuration.MaxNumberOfParallelIndexTasks).ToList();
			int start = 0;
			foreach (var partitioned in partitioneds)
			{
				context.CancellationToken.ThrowIfCancellationRequested();
				var currentStart = start;
				Parallel.ForEach(partitioned, new ParallelOptions
				{
					TaskScheduler = context.TaskScheduler,
					MaxDegreeOfParallelism = context.Configuration.MaxNumberOfParallelIndexTasks
				}, (item, _, index) =>
				{
					using(LogManager.OpenMappedContext("database", context.DatabaseName ?? Constants.SystemDatabase))
					using (new DisposableAction(() => LogContext.DatabaseName.Value = null))
					{
						LogContext.DatabaseName.Value = context.DatabaseName;
						action(item, currentStart + index);
					}
				});
				start += partitioned.Count;
			}
		}

		static IEnumerable<IList<T>> Partition<T>(IList<T> source, int size)
		{
			for (int i = 0; i < source.Count; i += size)
			{
				yield return source.Skip(i).Take(size).ToList();
			}
		}

		public void ExecuteAllInterleaved<T>(WorkContext context, IList<T> result, Action<T> action)
		{
			if (result.Count == 0)
				return;
			if (result.Count == 1)
			{
				action(result[0]);
				return;
			}

			using (LogManager.OpenMappedContext("database", context.DatabaseName ?? Constants.SystemDatabase))
			using (new DisposableAction(() => LogContext.DatabaseName.Value = null))
			using (var semaphoreSlim = new SemaphoreSlim(context.Configuration.MaxNumberOfParallelIndexTasks))
			{
				LogContext.DatabaseName.Value = context.DatabaseName ?? Constants.SystemDatabase;
				var tasks = new Task[result.Count];
				for (int i = 0; i < result.Count; i++)
				{
					var index = result[i];
					var indexToWorkOn = index;

					var task = new Task(() => action(indexToWorkOn));
					tasks[i] = task.ContinueWith(_ => semaphoreSlim.Release());

					semaphoreSlim.Wait();

					task.Start(context.Database.BackgroundTaskScheduler);
				}

				Task.WaitAll(tasks);
			}
		}
	}
}