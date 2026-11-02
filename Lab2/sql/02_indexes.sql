ALTER TABLE client_profiles ADD FOREIGN KEY (user_id) REFERENCES users(id);
ALTER TABLE freelancer_profiles ADD FOREIGN KEY (user_id) REFERENCES users(id);
ALTER TABLE projects ADD FOREIGN KEY (client_id) REFERENCES client_profiles(user_id);
ALTER TABLE projects ADD FOREIGN KEY (category_id) REFERENCES categories(id);
ALTER TABLE project_skills ADD FOREIGN KEY (project_id) REFERENCES projects(id);
ALTER TABLE project_skills ADD FOREIGN KEY (skill_id) REFERENCES skills(id);
ALTER TABLE project_attributes ADD FOREIGN KEY (project_id) REFERENCES projects(id);
ALTER TABLE proposals ADD FOREIGN KEY (project_id) REFERENCES projects(id);
ALTER TABLE proposals ADD FOREIGN KEY (freelancer_id) REFERENCES freelancer_profiles(user_id);

ALTER TABLE proposals ADD UNIQUE (project_id, freelancer_id);
CREATE UNIQUE INDEX ux_proposals_one_accepted ON proposals (project_id) WHERE status = 'accepted';
CREATE INDEX ix_proposals_freelancer ON proposals (freelancer_id, created_at DESC, id DESC);
CREATE INDEX ix_projects_catalog ON projects (status, created_at DESC) INCLUDE (budget);
CREATE INDEX ix_projects_search ON projects USING gin (search_vector);
CREATE INDEX ix_project_skills_skill ON project_skills (skill_id, project_id);
CREATE INDEX ix_project_attributes_kv ON project_attributes (key, value, project_id);
