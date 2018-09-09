﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EWork.Config;
using EWork.Exceptions;
using EWork.Models;
using EWork.Services.Interfaces;
using EWork.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace EWork.Controllers
{
    [Authorize]
    public class JobController : Controller
    {
        private readonly IFreelancingPlatform _freelancingPlatform;
        private readonly UserManager<User> _userManager;
        private readonly IHostingEnvironment _environment;
        private readonly IOptions<PhotoConfig> _photoOptions;

        public JobController(IFreelancingPlatform freelancingPlatform, 
            UserManager<User> userManager,
            IHostingEnvironment environment,
            IOptions<PhotoConfig> photoOptions)
        {
            _freelancingPlatform = freelancingPlatform;
            _userManager = userManager;
            _environment = environment;
            _photoOptions = photoOptions;
        }

        [Authorize(Roles = "employer, freelancer, moderator")]
        public IActionResult JobBoard(string requiredTags)
        {
            var jobs = _freelancingPlatform.JobManager.GetAll().Where(j => !j.IsClosed);
            var usedTags = Enumerable.Empty<string>();
            if (!(requiredTags is null))
            {
                // Tag length cannot be greater than 20.
                var tags = usedTags = requiredTags.Split(' ').Where(tag => tag.Length <= 20);
                jobs = jobs.Where(j => j.JobTags.Any(jt =>
                    tags.Any(tagText => jt.Tag.Text.Equals(tagText, StringComparison.InvariantCultureIgnoreCase))));
            }

            if (User.IsInRole("freelancer"))
                jobs = jobs.Where(j => j.HiredFreelancer == null);

            var jobBoardViewModel = new JobBoardViewModel(jobs, usedTags);
            return View(jobBoardViewModel);
        }

        [HttpPost]
        [Authorize(Roles = "employer, moderator")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> DeleteJob(int jobId)
        {
            var deletedJob = await _freelancingPlatform.JobManager.FindAsync(job => !job.IsClosed && job.Id == jobId);
            if (deletedJob is null)
                return BadRequest();

            var currentUser = await _userManager.GetUserAsync(User);
            if (deletedJob.Employer.Id != currentUser.Id)
                return BadRequest();

            await _freelancingPlatform.JobManager.DeleteAsync(deletedJob);
            return Ok();
        }

        public IActionResult CreateJob() => View();

        [HttpPost]
        [Authorize(Roles = "employer")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> CreateJob(Job job, string tags)
        {
            if (!ModelState.IsValid)
                return BadRequest();

            if (!(await _userManager.GetUserAsync(User) is Employer currentUser))
                return BadRequest();

            var platformBalance = await _freelancingPlatform.BalanceManager.GetFreelancingPlatformBalanceAsync();
            var curUserBalance = await _freelancingPlatform.BalanceManager.FindAsync(b => b.UserId == currentUser.Id);
            try
            {
                await _freelancingPlatform.BalanceManager.TransferMoneyAsync(curUserBalance, platformBalance,
                    job.Budget);
            }
            catch (NotEnoughMoneyException e)
            {
                return Content("Error!" + Environment.NewLine + e.Message);
            }

            job.Employer = currentUser;
            job.Proposals = new List<Proposal>();
            job.JobTags = new List<JobTags>();
            job.CreationDate = DateTime.Now;

            if (!(tags is null))
            {
                var splittedTags = tags.Trim().Split(' ').Where(tag => tag.Length <= 20);
                var newTags = await _freelancingPlatform.TagManager.AddTagsRangeAsync(splittedTags);
                foreach (var tag in newTags)
                    job.JobTags.Add(new JobTags { Tag = tag });
            }

            await _freelancingPlatform.JobManager.AddAsync(job);
            return Redirect("JobBoard");
        }

        [HttpGet]
        [Authorize(Roles = "employer, freelancer, moderator")]
        public async Task<IActionResult> JobInfo(int jobId)
        {
            var job = await _freelancingPlatform.JobManager.FindAsync(j => j.Id == jobId);
            if (job is null)
                return BadRequest();

            if (job.IsClosed)
                ViewBag.Heading = "Closed Job";

            Proposal proposal = null;
            User currentUser = null;
            if (User.IsInRole("freelancer"))
            {
                if (!(job.HiredFreelancer is null) && job.HiredFreelancer.Id != _userManager.GetUserId(User))
                    return BadRequest();

                currentUser = await _userManager.GetUserAsync(User);
                proposal = job.Proposals.Find(p => p.Sender.Id == currentUser.Id);
            }

            var pathToProfilePhotos = Path.Combine(_environment.ContentRootPath, _photoOptions.Value.UsersPhotosPath);
            var jobInfoViewModel = new JobInfoViewModel(currentUser, job, proposal, pathToProfilePhotos);
            return View(jobInfoViewModel);
        }

        

        [Authorize(Roles = "freelancer")]
        public async Task<IActionResult> FreelancerContracts()
        {
            if (!(await _userManager.GetUserAsync(User) is Freelancer currentUser))
                return BadRequest();

            var jobs = _freelancingPlatform.JobManager.GetAll()
                .Where(j => !j.IsClosed && j.HiredFreelancer.Id == currentUser.Id);

            ViewData["Title"] = "Contracts";
            ViewBag.Heading = "Your Contracts";
            return View("JobBoard", jobs);
        }

        [Authorize(Roles = "employer")]
        public async Task<IActionResult> OpenedJobs()
        {
            if (!(await _userManager.GetUserAsync(User) is Employer currentUser))
                return BadRequest();

            var jobs = _freelancingPlatform.JobManager.GetAll()
                .Where(j => !j.IsClosed && j.Employer.Id == currentUser.Id);

            ViewData["Title"] = "Jobs";
            ViewBag.Heading = "Opened Jobs";
            return View("JobBoard", jobs);
        }

        [Authorize(Roles = "employer")]
        public async Task<IActionResult> EmployerContracts()
        {
            if (!(await _userManager.GetUserAsync(User) is Employer currentUser))
                return BadRequest();

            var jobs = _freelancingPlatform.JobManager.GetAll()
                .Where(j => !j.IsClosed && j.Employer.Id == currentUser.Id && j.HiredFreelancer != null);

            ViewData["Title"] = "Contracts";
            ViewBag.Heading = "Your Contracts";
            return View("JobBoard", jobs);
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error() => 
            View(new ErrorViewModel { RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier });
    }
}
