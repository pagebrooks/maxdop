create procedure dbo.usp_TopReps @Start date,@End date as begin
set nocount on;
select top 10 r.RepName,sum(o.Total) as Revenue,
case when sum(o.Total)>100000 then 'gold' when sum(o.Total)>50000 then 'silver' else 'bronze' end as Tier
from dbo.Orders o join dbo.Reps r on r.RepId=o.RepId
where o.OrderDate>=@Start and o.OrderDate<@End and o.Status<>'cancelled'
group by r.RepName order by Revenue desc;
end
